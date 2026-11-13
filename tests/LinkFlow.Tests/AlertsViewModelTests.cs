using System.IO;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

public sealed class AlertsViewModelTests
{
    [Fact]
    public void Dashboard_caps_notices_and_dismisses_the_single_failsafe_banner()
    {
        var alerts = new AlertsViewModel();
        var snapshot = new EngineSnapshot
        {
            FailSafeActive = true,
            Alerts = [CreateAlert("failsafe", AlertSeverity.Critical), CreateAlert("pump", AlertSeverity.Critical),
                CreateAlert("liquid", AlertSeverity.Warning), CreateAlert("fan", AlertSeverity.Warning)]
        };
        alerts.Update(snapshot, serviceDisconnected: true);

        Assert.Equal("failsafe", alerts.Banner!.Key);
        Assert.Equal("pump", Assert.Single(alerts.Preview).Key);
        Assert.Equal(5, alerts.PendingCount);
        Assert.Equal("Histórico (+3)", alerts.HistoryLabel);

        alerts.DismissCommand.Execute(alerts.Banner);
        Assert.Equal("service-disconnected", alerts.Banner!.Key);
        Assert.DoesNotContain(alerts.Preview, alert => alert.Key == "failsafe");
        Assert.Contains(alerts.History, entry => entry.Alert.Key == "failsafe" && entry.Status == "Ativo · dispensado");
        Assert.True(snapshot.FailSafeActive);
        Assert.Equal(4, snapshot.Alerts.Count);
    }

    [Fact]
    public void Dismissal_survives_refresh_and_reopen_but_a_more_serious_or_new_alert_is_visible()
    {
        string path = Path.Combine(Path.GetTempPath(), $"linkflow-alerts-{Guid.NewGuid():N}.json");
        try
        {
            var notice = CreateAlert("liquid", AlertSeverity.Warning);
            var snapshot = new EngineSnapshot { Alerts = [notice] };
            var alerts = new AlertsViewModel(path);
            alerts.Update(snapshot, serviceDisconnected: false);
            alerts.DismissCommand.Execute(Assert.Single(alerts.Preview));

            notice.Message = "Nova leitura do líquido.";
            alerts = new AlertsViewModel(path);
            alerts.Update(snapshot, serviceDisconnected: false);
            Assert.False(alerts.HasPending);
            Assert.Equal("Ativo · dispensado", Assert.Single(alerts.History).Status);

            notice.Severity = AlertSeverity.Critical;
            alerts.Record(notice);
            alerts.Update(snapshot, serviceDisconnected: false);
            Assert.Equal(AlertSeverity.Critical, Assert.Single(alerts.Preview).Severity);
            Assert.Single(alerts.History);

            alerts.Update(new EngineSnapshot(), serviceDisconnected: false);
            Assert.Equal("Encerrado", Assert.Single(alerts.History).Status);
            notice.Since = notice.Since.AddMinutes(1);
            alerts.Update(snapshot, serviceDisconnected: false);
            Assert.True(alerts.HasPending);
            Assert.Equal(2, alerts.History.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void History_keeps_the_fifty_most_recent_occurrences()
    {
        var alerts = new AlertsViewModel();
        var notice = CreateAlert("temperature", AlertSeverity.Warning);
        for (int index = 0; index < 65; index++)
        {
            notice.Since = notice.Since.AddSeconds(1);
            alerts.Record(notice);
        }

        Assert.Equal(50, alerts.History.Count);
        Assert.Equal(notice.Since, alerts.History[0].Alert.Since);
        Assert.Equal(notice.Since.AddSeconds(-49), alerts.History[^1].Alert.Since);
    }

    private static EngineAlert CreateAlert(string key, AlertSeverity severity) => new()
    {
        Key = key,
        Since = new DateTime(2026, 1, 1),
        Severity = severity,
        Title = key,
        Message = "Mensagem do alerta."
    };
}
