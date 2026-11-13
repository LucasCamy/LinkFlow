using System.Runtime.InteropServices;

namespace LinkFlow.App.Services;

internal sealed class WindowsUpdateComScope : IDisposable
{
    private readonly Stack<object> _objects = new();

    public dynamic CreateSession()
    {
        dynamic session = Create("Microsoft.Update.Session");
        session.ClientApplicationID = "LinkFlow";
        return session;
    }

    public dynamic Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException("O Agente do Windows Update está indisponível.");
        var instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("O Agente do Windows Update não respondeu.");
        return Keep(instance);
    }

    public dynamic Keep(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _objects.Push(value);
        return value;
    }

    public void Dispose()
    {
        while (_objects.TryPop(out var value)) Marshal.ReleaseComObject(value);
    }
}
