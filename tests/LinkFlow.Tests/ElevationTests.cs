using LinkFlow.Core;
using Xunit;

namespace LinkFlow.Tests;

public class ElevationTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, null, true)]
    public void LogitechSdkIsSkippedOnlyWhenElevatedWithoutAnElevatedAgent(bool self, bool? agent, bool blocked)
    {
        Assert.Equal(blocked, LogitechPeripheralsRgb.IsBlockedByElevation(self, agent));
    }

    [Fact]
    public void ElevatedDiagnosticTellsHowToReopen()
    {
        Assert.Contains("administrador", LogitechPeripheralsRgb.ElevatedDiagnostic);
        Assert.Contains("menu Iniciar", LogitechPeripheralsRgb.ElevatedDiagnostic);
    }
}
