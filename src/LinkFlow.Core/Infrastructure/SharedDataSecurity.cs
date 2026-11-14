using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.Core;

/// <summary>
/// Protege %ProgramData%\LinkFlow, onde o serviço (SYSTEM) e o app elevado gravam configuração e logs.
/// Sem isso, a pasta herda do ProgramData o direito de usuários comuns criarem arquivos e pastas, e quem a
/// criou primeiro (o app sem administrador) fica como dono. Um usuário poderia trocar a pasta, os logs ou o
/// arquivo temporário da configuração por um link e fazer o SYSTEM gravar ou apagar arquivos em qualquer lugar.
/// </summary>
public static class SharedDataSecurity
{
    private const int MaxDepth = 6;
    private const int MaxEntries = 20_000;

    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Só SYSTEM ou administrador conseguem (e precisam) aplicar a proteção.</summary>
    public static bool IsPrivileged
    {
        get
        {
            try
            {
                using var me = WindowsIdentity.GetCurrent();
                return me.IsSystem || new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Deixa a pasta com dono Administradores e ACL própria (SYSTEM e Administradores: controle total;
    /// Usuários: leitura). Remove links (junções/symlinks) plantados dentro dela e devolve a posse dos itens
    /// criados por usuários. Em <c>media</c> os usuários só podem criar arquivos novos (importar imagens).
    /// Chamar antes de qualquer gravação do processo privilegiado, inclusive o log.
    /// </summary>
    /// <returns>Mensagens para o log (gravadas pelo chamador depois que a pasta está protegida).</returns>
    public static IReadOnlyList<string> Harden(string sharedDir)
    {
        var notes = new List<string>();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sharedDir));

        if (IsReparsePoint(root))
        {
            // Directory.Delete sem recursão remove só o link, nunca o destino
            Directory.Delete(root);
            notes.Add($"Segurança: {root} era um link e foi removido.");
        }
        Directory.CreateDirectory(root);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var rootSecurity = new DirectorySecurity();
        rootSecurity.SetOwner(Admins);
        rootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        rootSecurity.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        rootSecurity.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        rootSecurity.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(rootSecurity);

        int entries = 0;
        SanitizeChildren(root, 0, ref entries, notes);

        foreach (var sub in new[] { "logs", "media" })
        {
            var dir = Path.Combine(root, sub);
            Directory.CreateDirectory(dir);
            var security = InheritedOnly(new DirectorySecurity());
            if (sub == "media")
            {
                // A interface (usuário comum) copia as imagens do LCD para cá; sem criar pastas nem apagar
                security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.CreateFiles,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(dir).SetAccessControl(security);
        }
        return notes;
    }

    /// <summary>Aplica a proteção quando o processo tem privilégio; falhas não impedem o controle das ventoinhas.</summary>
    public static void TryHarden(string sharedDir)
    {
        if (!IsPrivileged) return;
        try
        {
            foreach (var note in Harden(sharedDir)) Log.Warn(note);
        }
        catch (Exception ex)
        {
            Log.Error($"Não foi possível proteger {sharedDir}", ex);
        }
    }

    private static T InheritedOnly<T>(T security) where T : FileSystemSecurity
    {
        security.SetOwner(Admins);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        return security;
    }

    private static void SanitizeChildren(string dir, int depth, ref int entries, List<string> notes)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            if (++entries > MaxEntries) throw new InvalidOperationException($"Itens demais em {dir}.");
            var attributes = File.GetAttributes(path);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (isDirectory) Directory.Delete(path);
                else File.Delete(path);
                notes.Add($"Segurança: link removido de {path}.");
                continue;
            }

            if (isDirectory)
            {
                new DirectoryInfo(path).SetAccessControl(InheritedOnly(new DirectorySecurity()));
                if (depth + 1 >= MaxDepth) throw new InvalidOperationException($"Pastas aninhadas demais em {path}.");
                SanitizeChildren(path, depth + 1, ref entries, notes);
            }
            else
            {
                new FileInfo(path).SetAccessControl(InheritedOnly(new FileSecurity()));
            }
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    // ------------------------------------------------------------------ Verificações por handle

    /// <summary>
    /// Confere pelo handle já aberto que o arquivo está mesmo dentro de <paramref name="directory"/> (resolvendo
    /// links de qualquer parte do caminho) e não é um hard link para outro arquivo do sistema.
    /// </summary>
    public static bool IsPlainFileInside(SafeFileHandle file, string directory, out string? reason)
    {
        reason = null;
        if (!GetFileInformationByHandle(file, out var info))
        {
            reason = $"não foi possível ler os atributos ({new Win32Exception(Marshal.GetLastWin32Error()).Message})";
            return false;
        }
        if (info.NumberOfLinks != 1 || (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            reason = "o arquivo é um link";
            return false;
        }

        var filePath = FinalPath(file);
        using var dirHandle = CreateFileW(directory, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        var dirPath = dirHandle.IsInvalid ? null : FinalPath(dirHandle);
        if (filePath == null || dirPath == null)
        {
            reason = "não foi possível resolver o caminho real";
            return false;
        }
        if (!filePath.StartsWith(Path.TrimEndingDirectorySeparator(dirPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            reason = "o caminho real fica fora da biblioteca de mídia";
            return false;
        }
        return true;
    }

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        return new string(buffer, 0, (int)length);
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}

/// <summary>
/// Leitura das imagens do LCD. O serviço roda como SYSTEM e recebe a configuração de qualquer processo da
/// sessão interativa: só aceita arquivos da biblioteca (%ProgramData%\LinkFlow\media), nunca caminhos de rede
/// (o SYSTEM se autenticaria com a conta da máquina) e com tamanho limitado.
/// </summary>
public static class MediaFiles
{
    public const long MaxBytes = 64L * 1024 * 1024;
    public const long MaxPixels = 8192L * 8192;

    /// <summary>Validação só pelo texto (não toca no disco nem na rede).</summary>
    public static bool IsAllowedPath(string? path, bool libraryOnly, out string fullPath, out string? reason)
    {
        fullPath = string.Empty;
        reason = null;
        if (string.IsNullOrWhiteSpace(path)) { reason = "caminho vazio"; return false; }
        // UNC (\\servidor), dispositivos (\\.\, \\?\) e caminhos relativos ficam de fora
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            reason = "caminho de rede, de dispositivo ou relativo";
            return false;
        }
        // "C:\foto.png:fluxo" (fluxo alternativo do NTFS)
        if (path.IndexOf(':', 2) >= 0) { reason = "fluxo alternativo"; return false; }

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "caminho inválido";
            return false;
        }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) { reason = "caminho de rede"; return false; }
        if (libraryOnly && !IsInside(full, AppPaths.MediaDir)) { reason = "fora da biblioteca de mídia do LinkFlow"; return false; }

        fullPath = full;
        return true;
    }

    public static bool IsInside(string fullPath, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Indica se vale tentar de novo um arquivo que não abriu antes (ele pode ter aparecido depois).</summary>
    public static bool Exists(string path, bool libraryOnly) =>
        IsAllowedPath(path, libraryOnly, out var full, out _) && File.Exists(full);

    /// <summary>Lê o arquivo inteiro aplicando as regras acima; null com o motivo quando recusado.</summary>
    public static byte[]? TryRead(string path, bool libraryOnly, out string? reason)
    {
        if (!IsAllowedPath(path, libraryOnly, out var full, out reason)) return null;
        if (!File.Exists(full)) { reason = "arquivo não encontrado"; return null; }

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
        if (libraryOnly && !SharedDataSecurity.IsPlainFileInside(stream.SafeFileHandle, AppPaths.MediaDir, out reason)) return null;

        long length = stream.Length;
        if (length <= 0 || length > MaxBytes)
        {
            reason = $"tamanho fora do limite ({length / 1024 / 1024} MB; máximo {MaxBytes / 1024 / 1024} MB)";
            return null;
        }
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
