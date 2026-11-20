using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using LinkFlow.Core;
using Microsoft.CSharp.RuntimeBinder;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

internal sealed record WindowsDiagnosticEntry(string Title, string Value, string? Details = null);

internal sealed record WindowsDiagnosticResult(string Summary, IReadOnlyList<WindowsDiagnosticEntry> Entries, string? Warning = null);

internal static class WindowsDiagnostics
{
    private const int MaxEntries = 30;
    private const int EventDays = 7;

    public static WindowsDiagnosticResult ReadSystem()
    {
        var now = DateTimeOffset.Now;
        var elapsed = TimeSpan.FromMilliseconds(GetTickCount64());
        var uptime = $"{(elapsed.Days > 0 ? $"{elapsed.Days} d " : string.Empty)}{elapsed.Hours} h {elapsed.Minutes} min";
        var rows = new List<WindowsDiagnosticEntry>
        {
            new(Strings.Win_TempoDesdeInicializacao, uptime, string.Format(Strings.Win_InicioEstimado, (now - elapsed).ToLocalTime()))
        };
        var warnings = new List<string>();

        try
        {
            using var com = new WindowsUpdateComScope();
            dynamic system = com.Create("Microsoft.Update.SystemInfo");
            rows.Add(new(Strings.Win_ReinicioPeloWindowsUpdate, (bool)system.RebootRequired ? Strings.Win_Necessario : Strings.Win_SemPendencia));
        }
        catch (Exception ex) when (IsUpdateReadError(ex))
        {
            Log.Warn($"Falha ao consultar o reinício pendente: {ex.Message}");
            rows.Add(new(Strings.Win_ReinicioPeloWindowsUpdate, Strings.Win_Indisponivel));
            warnings.Add(Strings.Win_EstadoReinicioWindowsUpdate);
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        bool volumesIncomplete = false;
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Falha ao consultar os volumes: {ex.Message}");
            drives = [];
            volumesIncomplete = true;
        }

        int systemRows = rows.Count;
        foreach (var drive in drives.OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;

                string volume = string.Format(Strings.Win_Volume, drive.Name.TrimEnd('\\'));
                if (string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)) volume += Strings.Win_Sistema;
                long total = drive.TotalSize;
                long free = drive.TotalFreeSpace;
                if (total <= 0)
                {
                    volumesIncomplete = true;
                    continue;
                }
                double used = Math.Clamp((total - free) * 100d / total, 0, 100);
                const double gib = 1L << 30;
                rows.Add(new(volume, string.Format(Strings.Win_GiBLivresGiB, free / gib, total / gib), string.Format(Strings.Win_Ocupado, used, drive.DriveFormat)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Falha ao consultar o volume {drive.Name}: {ex.Message}");
                volumesIncomplete = true;
            }
        }
        if (volumesIncomplete) warnings.Add(Strings.Win_ParteLeituraEspacoDisco);
        else if (rows.Count == systemRows) rows.Add(new(Strings.Win_VolumesFixos, Strings.Win_NenhumVolumeFixoEncontrado));

        return new(string.Format(Strings.Win_Consultado, now), rows, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    public static WindowsDiagnosticResult ReadUpdateHistory()
    {
        using var com = new WindowsUpdateComScope();
        dynamic session = com.CreateSession();
        dynamic searcher = com.Keep((object)session.CreateUpdateSearcher());
        int count = Math.Min((int)searcher.GetTotalHistoryCount(), MaxEntries);
        if (count <= 0) return new(Strings.Win_NenhumRegistroHistoricoLocal, []);

        dynamic history = com.Keep((object)searcher.QueryHistory(0, count));
        var rows = new List<WindowsDiagnosticEntry>(count);
        int returned = Math.Min((int)history.Count, count);
        for (int i = 0; i < returned; i++)
        {
            try
            {
                dynamic item = com.Keep((object)history.Item(i));
                string? title = item.Title;
                string operation = (int)item.Operation switch
                {
                    1 => Strings.Win_Instalacao,
                    2 => Strings.Win_Desinstalacao,
                    _ => Strings.Win_Operacao
                };
                string result = (int)item.ResultCode switch
                {
                    0 => Strings.Win_NaoIniciada,
                    1 => Strings.Win_Andamento,
                    2 => Strings.Win_Concluida,
                    3 => Strings.Win_ConcluidaErros,
                    4 => Strings.Win_Falha,
                    5 => Strings.Win_Cancelada,
                    _ => Strings.Win_ResultadoDesconhecido
                };
                // O WUA retorna a data em UTC, sem fuso na representação COM.
                var localDate = DateTime.SpecifyKind((DateTime)item.Date, DateTimeKind.Utc).ToLocalTime();
                int hresult = item.HResult;
                string details = hresult == 0 ? $"{localDate:g}" : $"{localDate:g} · 0x{hresult:X8}";
                rows.Add(new(string.IsNullOrWhiteSpace(title) ? Strings.Win_AtualizacaoSemTitulo : title, $"{operation} · {result}", details));
            }
            catch (Exception ex) when (IsUpdateReadError(ex))
            {
                Log.Warn($"Falha ao ler um registro do Windows Update: {ex.Message}");
            }
        }

        string summary = rows.Count switch
        {
            0 => Strings.Win_NenhumRegistroPodeSer,
            1 => Strings.Win_1RegistroRecenteHistorico,
            _ => string.Format(Strings.Win_RegistrosRecentesHistoricoLocal, rows.Count)
        };
        string? warning = rows.Count < returned ? Strings.Win_AlgunsRegistrosWindowsUpdate : null;
        return new(summary, rows, warning);
    }

    public static WindowsDiagnosticResult ReadSystemEvents()
    {
        long window = (long)TimeSpan.FromDays(EventDays).TotalMilliseconds;
        var filter = FormattableString.Invariant($"""
            *[System[TimeCreated[timediff(@SystemTime) >= 0 and timediff(@SystemTime) <= {window}] and
                (Level=1 or Level=2 or (Level=3 and
                    (Provider[@Name='Microsoft-Windows-WHEA-Logger'] or Provider[@Name='disk'] or
                     Provider[@Name='stornvme'] or Provider[@Name='storahci'] or Provider[@Name='Ntfs'] or
                     Provider[@Name='Microsoft-Windows-Ntfs'] or Provider[@Name='Microsoft-Windows-StorPort'])))]]
            """);
        var query = new EventLogQuery("System", PathType.LogName, filter) { ReverseDirection = true };
        var rows = new List<WindowsDiagnosticEntry>(MaxEntries);
        string? warning = null;
        try
        {
            using var reader = new EventLogReader(query);
            while (rows.Count < MaxEntries)
            {
                using var item = reader.ReadEvent();
                if (item == null) break;
                string level = item.Level switch { 1 => Strings.Set_Critico, 2 => Strings.Win_Erro, _ => Strings.Win_Aviso };
                string value = item.TimeCreated is { } time ? $"{level} · {time:g}" : level;
                rows.Add(new(string.Format(Strings.Win_EventSource, item.ProviderName ?? Strings.Win_SystemName, item.Id), value, EventDescription(item)));
            }
        }
        catch (EventLogException ex) when (rows.Count > 0)
        {
            Log.Warn($"Leitura parcial de eventos do sistema: {ex.Message}");
            warning = Strings.Win_LeituraEventosFoiInterrompida;
        }

        string summary = rows.Count switch
        {
            0 => string.Format(Strings.Win_NenhumEventoCorrespondenteUltimos, EventDays),
            1 => string.Format(Strings.Win_1EventoUltimosDias, EventDays),
            _ => string.Format(Strings.Win_EventosMaisRecentesUltimos, rows.Count, EventDays)
        };
        return new(summary, rows, warning);
    }

    private static string? EventDescription(EventRecord item)
    {
        try
        {
            const int maxLength = 850;
            string? message = item.FormatDescription()?.Trim();
            return message is { Length: > maxLength } ? $"{message[..maxLength]}…" : message;
        }
        catch (EventLogException)
        {
            return null;
        }
    }

    private static bool IsUpdateReadError(Exception ex) => ex is COMException or UnauthorizedAccessException or
        RuntimeBinderException or InvalidCastException or ArgumentNullException or InvalidOperationException;

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
