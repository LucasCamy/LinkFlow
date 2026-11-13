using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LinkFlow.App.Services;

internal sealed record PowerPlan(Guid Id, string Name)
{
    public override string ToString() => Name;
}

internal enum PowerIdleAction { TurnOffDisplay, Sleep }

internal sealed record PowerIdleTimeout(PowerIdleAction Action, bool OnBattery, uint Seconds);

internal sealed record WindowsPowerState(IReadOnlyList<PowerPlan> Plans, Guid ActivePlan,
    IReadOnlyList<PowerIdleTimeout> IdleTimeouts, bool Animations)
{
    public uint? Timeout(PowerIdleAction action, bool onBattery) =>
        IdleTimeouts.FirstOrDefault(timeout => timeout.Action == action && timeout.OnBattery == onBattery)?.Seconds;
}

internal static class WindowsPower
{
    private const uint AccessScheme = 16;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;
    private const uint GetClientAreaAnimation = 0x1042;
    private const uint SetClientAreaAnimation = 0x1043;
    private const uint PersistAndBroadcast = 3;
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    public static WindowsPowerState Read()
    {
        var active = GetActivePlan();
        var plans = new List<PowerPlan>();
        for (uint index = 0; ; index++)
        {
            uint length = 16;
            uint code = PowerEnumerate(0, 0, 0, AccessScheme, index, out Guid id, ref length);
            if (code == ErrorNoMoreItems) break;
            Check(code);
            plans.Add(new PowerPlan(id, ReadName(id)));
        }

        var battery = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus;
        bool hasBattery = battery != System.Windows.Forms.BatteryChargeStatus.Unknown &&
                          !battery.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery);
        var timeouts = new List<PowerIdleTimeout>(4);
        foreach (var action in new[] { PowerIdleAction.TurnOffDisplay, PowerIdleAction.Sleep })
        {
            if (ReadIdleTimeout(active, action, onBattery: false) is { } ac) timeouts.Add(new PowerIdleTimeout(action, false, ac));
            if (hasBattery && ReadIdleTimeout(active, action, onBattery: true) is { } dc) timeouts.Add(new PowerIdleTimeout(action, true, dc));
        }
        return new WindowsPowerState(plans, active, timeouts, ReadAnimations());
    }

    public static void Activate(Guid id, Guid expectedActive)
    {
        if (GetActivePlan() != expectedActive)
            throw new InvalidOperationException("O plano ativo mudou. Leia os ajustes novamente.");
        Check(PowerSetActiveScheme(0, in id));
        if (GetActivePlan() != id) throw new InvalidOperationException("O Windows não confirmou a troca do plano.");
    }

    public static void SetIdleTimeout(Guid plan, PowerIdleAction action, bool onBattery, uint expected, uint seconds)
    {
        if (GetActivePlan() != plan)
            throw new InvalidOperationException("O plano ativo mudou. Leia os ajustes novamente.");
        if (ReadIdleTimeout(plan, action, onBattery) != expected)
            throw new InvalidOperationException("O tempo de inatividade mudou. Leia os ajustes novamente.");

        Check(WriteIdleTimeout(plan, action, onBattery, seconds));
        try
        {
            // O plano precisa ser reaplicado para o Windows usar o novo tempo.
            Activate(plan, plan);
            if (ReadIdleTimeout(plan, action, onBattery) != seconds)
                throw new InvalidOperationException("O Windows não confirmou o tempo de inatividade.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            uint rollback = ReadIdleTimeout(plan, action, onBattery) == seconds ? WriteIdleTimeout(plan, action, onBattery, expected) : 0;
            string message = rollback == 0 ? "O Windows não conseguiu aplicar o tempo. Leia os ajustes novamente." :
                "O Windows não conseguiu aplicar o tempo nem restaurar o valor salvo no plano.";
            throw new InvalidOperationException(message, ex);
        }
    }

    public static void SetAnimations(bool enabled, bool expected)
    {
        if (ReadAnimations() != expected)
            throw new InvalidOperationException("As animações foram alteradas no Windows. Leia os ajustes novamente.");
        if (!WriteSystemParameter(SetClientAreaAnimation, 0, enabled ? 1 : 0, PersistAndBroadcast))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (ReadAnimations() != enabled)
            throw new InvalidOperationException("O Windows manteve a configuração de animações anterior.");
    }

    private static bool ReadAnimations()
    {
        if (!ReadSystemParameter(GetClientAreaAnimation, 0, out bool enabled, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return enabled;
    }

    private static Guid GetActivePlan()
    {
        Check(PowerGetActiveScheme(0, out nint pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    private static string ReadName(Guid id)
    {
        uint length = 0;
        uint code = PowerReadFriendlyName(0, in id, 0, 0, null, ref length);
        if (code is not (0 or ErrorMoreData) || length == 0) return id.ToString();
        var bytes = new byte[length];
        code = PowerReadFriendlyName(0, in id, 0, 0, bytes, ref length);
        return code == 0 ? Encoding.Unicode.GetString(bytes, 0, (int)length).TrimEnd('\0') : id.ToString();
    }

    private static uint? ReadIdleTimeout(Guid id, PowerIdleAction action, bool onBattery)
    {
        var (subgroup, setting) = IdleSetting(action);
        uint code = onBattery
            ? PowerReadDCValueIndex(0, in id, in subgroup, in setting, out uint seconds)
            : PowerReadACValueIndex(0, in id, in subgroup, in setting, out seconds);
        return code == 0 ? seconds : null;
    }

    private static uint WriteIdleTimeout(Guid id, PowerIdleAction action, bool onBattery, uint seconds)
    {
        var (subgroup, setting) = IdleSetting(action);
        return onBattery ? PowerWriteDCValueIndex(0, in id, in subgroup, in setting, seconds) :
            PowerWriteACValueIndex(0, in id, in subgroup, in setting, seconds);
    }

    private static (Guid Subgroup, Guid Setting) IdleSetting(PowerIdleAction action) =>
        action == PowerIdleAction.Sleep ? (SleepSubgroup, StandbyIdle) : (VideoSubgroup, VideoIdle);

    private static void Check(uint code)
    {
        if (code != 0) throw new Win32Exception((int)code);
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(nint root, nint scheme, nint subgroup, uint access, uint index, out Guid buffer, ref uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint root, out nint active);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(nint root, in Guid scheme, nint subgroup, nint setting, [Out] byte[]? buffer, ref uint size);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(nint root, in Guid scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(nint root, in Guid scheme, in Guid subgroup, in Guid setting, out uint seconds);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(nint root, in Guid scheme, in Guid subgroup, in Guid setting, out uint seconds);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(nint root, in Guid scheme, in Guid subgroup, in Guid setting, uint seconds);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(nint root, in Guid scheme, in Guid subgroup, in Guid setting, uint seconds);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadSystemParameter(uint action, uint value, [MarshalAs(UnmanagedType.Bool)] out bool data, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteSystemParameter(uint action, uint value, nint data, uint flags);
}
