using System.IO;
using LinkFlow.App.Services;

namespace LinkFlow.Tests;

public sealed class UiSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkflow-ui-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldUser = AppPaths.UserDir;

    public UiSettingsTests() => AppPaths.UserDir = _dir;

    public void Dispose()
    {
        AppPaths.UserDir = _oldUser;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Fresh_install_starts_without_advanced_system_tools()
    {
        Assert.False(new UiSettingsService().Current.AdvancedSystemTools);
    }

    [Fact]
    public void Upgrade_keeps_advanced_system_tools_visible()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AppPaths.UiSettingsFile, """{ "closeToTray": true, "selectedPage": 8 }""");
        Assert.True(new UiSettingsService().Current.AdvancedSystemTools);
    }

    [Fact]
    public void Explicit_choice_is_kept_on_upgrade()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(AppPaths.UiSettingsFile, """{ "advancedSystemTools": false }""");
        Assert.False(new UiSettingsService().Current.AdvancedSystemTools);
    }
}
