using System.Globalization;
using System.Runtime.InteropServices;
using LinkFlow.Core;
using Microsoft.CSharp.RuntimeBinder;

namespace LinkFlow.App.Services;

internal readonly record struct DriverIdentity(Guid Id, int Revision);

internal sealed record DriverUpdate(DriverIdentity Identity, string Title, string Details, string? LicenseTerms, string? InstallIssue)
{
    public bool CanInstall => InstallIssue == null;
}

internal sealed record DriverSearchResult(IReadOnlyList<DriverUpdate> Updates, bool Incomplete);

internal enum DriverInstallStatus
{
    Installed = 0,
    InstalledRequiresRestart = 10,
    NoLongerOffered,
    RestartPending,
    InstallerBusy,
    RequiresReview,
    DownloadFailed,
    InstallationFailed
}

internal static class WindowsDrivers
{
    private const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Driver'";
    private const int Succeeded = 2;
    private const int SucceededWithErrors = 3;

    public static DriverSearchResult SearchAvailable()
    {
        using var com = new WindowsUpdateComScope();
        dynamic session = com.CreateSession();
        dynamic result = Search(com, (object)session, Criteria);
        dynamic updates = com.Keep((object)result.Updates);
        var found = new List<DriverUpdate>();
        bool incomplete = (int)result.ResultCode == SucceededWithErrors;
        int count = updates.Count;
        for (int i = 0; i < count; i++)
        {
            try
            {
                dynamic update = com.Keep((object)updates.Item(i));
                var driver = (IWindowsDriverUpdate)(object)update;
                if (IsFirmware(driver)) continue;

                dynamic identity = com.Keep((object)update.Identity);
                dynamic behavior = com.Keep((object)update.InstallationBehavior);
                bool accepted = update.EulaAccepted;
                string? terms = accepted ? null : (string?)update.EulaText;
                string? issue = (bool)behavior.CanRequestUserInput
                    ? "O instalador deste driver pode exigir interação no Windows Update."
                    : !accepted && string.IsNullOrWhiteSpace(terms) ? "Os termos de licença estão indisponíveis." : null;
                string details = string.Join(" · ", new[] { driver.DriverProvider, driver.DriverModel, driver.DriverHardwareId }
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
                found.Add(new DriverUpdate(new DriverIdentity(Guid.Parse((string)identity.UpdateID), (int)identity.RevisionNumber),
                    (string)update.Title, details, terms, issue));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException or RuntimeBinderException)
            {
                incomplete = true;
                Log.Warn($"Uma oferta do Windows Update não pôde ser lida: {ex.Message}");
            }
        }
        return new DriverSearchResult(found.OrderBy(update => update.Title).ToArray(), incomplete);
    }

    public static Task<int?> InstallAsync(DriverIdentity driver, bool acceptLicense) => Task.Run<int?>(() =>
    {
        if (AutoStartManager.IsElevated) return InstallElevated(driver, acceptLicense);
        string args = FormattableString.Invariant($"--install-driver {driver.Id:D} {driver.Revision}");
        if (acceptLicense) args += " --accept-driver-license";
        return ServiceManager.StartElevated(args, wait: true, out int code) ? code : null;
    });

    public static int InstallElevated(DriverIdentity driver, bool acceptLicense)
    {
        if (!AutoStartManager.IsElevated) return unchecked((int)0x80070005);
        try
        {
            using var com = new WindowsUpdateComScope();
            dynamic session = com.CreateSession();
            string criteria = Criteria + string.Format(CultureInfo.InvariantCulture,
                " and UpdateID='{0:D}' and RevisionNumber={1}", driver.Id, driver.Revision);
            dynamic searchResult = Search(com, (object)session, criteria);
            dynamic offered = com.Keep((object)searchResult.Updates);
            if ((int)offered.Count != 1) return (int)DriverInstallStatus.NoLongerOffered;
            dynamic update = com.Keep((object)offered.Item(0));
            dynamic identity = com.Keep((object)update.Identity);
            if (!Guid.TryParse((string)identity.UpdateID, out Guid id) || id != driver.Id || (int)identity.RevisionNumber != driver.Revision)
                return (int)DriverInstallStatus.NoLongerOffered;
            dynamic behavior = com.Keep((object)update.InstallationBehavior);
            if (IsFirmware((IWindowsDriverUpdate)(object)update) || (bool)behavior.CanRequestUserInput)
                return (int)DriverInstallStatus.RequiresReview;

            dynamic collection = com.Create("Microsoft.Update.UpdateColl");
            collection.Add(update);
            dynamic installer = com.Keep((object)session.CreateUpdateInstaller());
            installer.Updates = collection;
            installer.AllowSourcePrompts = false;
            ((IUpdateInstaller2)(object)installer).ForceQuiet = true;
            if ((bool)installer.IsBusy) return (int)DriverInstallStatus.InstallerBusy;
            if ((bool)installer.RebootRequiredBeforeInstallation) return (int)DriverInstallStatus.RestartPending;

            if (!(bool)update.EulaAccepted)
            {
                if (!acceptLicense || string.IsNullOrWhiteSpace((string?)update.EulaText)) return (int)DriverInstallStatus.RequiresReview;
                update.AcceptEula();
            }

            if (!(bool)update.IsDownloaded)
            {
                dynamic downloader = com.Keep((object)session.CreateUpdateDownloader());
                downloader.Updates = collection;
                dynamic download = com.Keep((object)downloader.Download());
                dynamic downloadResult = com.Keep((object)download.GetUpdateResult(0));
                if (!(bool)update.IsDownloaded || (int)downloadResult.ResultCode is not (Succeeded or SucceededWithErrors))
                    return Failed("baixar", (int)downloadResult.HResult, DriverInstallStatus.DownloadFailed);
            }

            dynamic installation = com.Keep((object)installer.Install());
            dynamic installationResult = com.Keep((object)installation.GetUpdateResult(0));
            if ((int)installationResult.ResultCode != Succeeded)
                return Failed("instalar", (int)installationResult.HResult, DriverInstallStatus.InstallationFailed);

            return (bool)installation.RebootRequired ? (int)DriverInstallStatus.InstalledRequiresRestart : (int)DriverInstallStatus.Installed;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao instalar driver pelo Windows Update", ex);
            return ex is COMException comError ? comError.ErrorCode : (int)DriverInstallStatus.InstallationFailed;
        }
    }

    private static int Failed(string action, int code, DriverInstallStatus fallback)
    {
        Log.Warn($"O Windows Update falhou ao {action} o driver (0x{code:X8}).");
        return code < 0 ? code : (int)fallback;
    }

    private static bool IsFirmware(IWindowsDriverUpdate driver) =>
        string.Equals(driver.DriverClass, "Firmware", StringComparison.OrdinalIgnoreCase);

    private static dynamic Search(WindowsUpdateComScope com, object session, string criteria)
    {
        dynamic searcher = com.Keep((object)((dynamic)session).CreateUpdateSearcher());
        searcher.Online = true;
        searcher.CanAutomaticallyUpgradeService = false;
        dynamic result = com.Keep((object)searcher.Search(criteria));
        if ((int)result.ResultCode is not (Succeeded or SucceededWithErrors))
            throw new InvalidOperationException("O Windows Update não concluiu a busca por drivers.");
        return result;
    }

    // Propriedades específicas de drivers no contrato de automação do wuapi.dll.
    [ComImport, Guid("b383cd1a-5ce9-4504-9f63-764b1236f191"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWindowsDriverUpdate
    {
        [DispId(0x60030001)] string? DriverClass { get; }
        [DispId(0x60030002)] string? DriverHardwareId { get; }
        [DispId(0x60030004)] string? DriverModel { get; }
        [DispId(0x60030005)] string? DriverProvider { get; }
    }

    [ComImport, Guid("3442d4fe-224d-4cee-98cf-30e0c4d229e6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IUpdateInstaller2
    {
        [DispId(0x60030001)] bool ForceQuiet { set; }
    }
}
