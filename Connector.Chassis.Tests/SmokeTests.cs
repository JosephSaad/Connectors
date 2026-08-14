// SmokeTests.cs
// -------------
// Proves the harness itself works before anything is built on it: the suite
// runs, it can see the chassis, and it can see chassis INTERNALS (several
// modules expose their seams as internal, so a suite without
// InternalsVisibleTo could only test the public rind).

namespace Connector.Chassis.Tests;

public class SmokeTests
{
    [Fact]
    public void TheSuiteCanSeeThePublicChassisSurface()
    {
        Assert.False(string.IsNullOrWhiteSpace(Alerting.WebhookUrlEnvVar));
    }

    [Fact]
    public void TheSuiteCanSeeChassisInternals()
    {
        // internal — visible only via InternalsVisibleTo. If this stops
        // compiling, the csproj entry was dropped and most of the suite's reach
        // went with it.
        Assert.NotEmpty(LogPruner.LedgerFilePatterns);
    }
}
