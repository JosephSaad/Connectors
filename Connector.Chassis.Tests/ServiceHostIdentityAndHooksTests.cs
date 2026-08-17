// ServiceHostIdentityAndHooksTests.cs
// -----------------------------------
// Pins the two things the ServiceHost consolidation had to get right.
//
// Four connectors kept their own ServiceHost. They were identical in MECHANISM
// — SCM handshake, working directory, graceful chunk-boundary stop — and
// differed only in identity strings and in what they told the Windows Event
// Log. The mechanism moved to the chassis; the identity became ChassisIdentity
// fields and the Event Log wording became host-supplied hooks.
//
// Both halves are easy to get subtly wrong in a way no build catches:
//
//   * The chassis ServiceHost previously hardcoded SEISMIC_CONNECTOR_HOME. Read
//     by any other connector, that resolves to nothing and the service silently
//     runs in %WINDIR%\System32 — where config/, env/, logs/ and data/ do not
//     exist. HomeEnvVar is therefore identity, not a constant.
//
//   * A hook left null must emit NOTHING. Salesforce and Hadoop deliberately
//     wrote no Event Log entry for service start/stop, so a chassis that helpfully
//     emitted one would be adding behaviour to two connectors, not sharing it.
//
// RunAsync itself is not exercised here: it builds a generic host and blocks on
// the SCM handshake, which is not a unit-test shape. What is tested is the
// identity resolution it reads and the hook contract it invokes.

namespace Connector.Chassis.Tests;

public class ServiceHostIdentityAndHooksTests : IDisposable
{
    private readonly ChassisIdentity _previous = Chassis.Identity;

    public void Dispose()
    {
        Chassis.Init(_previous);
        ServiceHost.ResetHooksForTests();
    }

    // ── identity ─────────────────────────────────────────────────────────────

    [Fact]
    public void ServiceName_DefaultsToTheEventLogSource()
    {
        // The positional constructor must stay source-compatible: hosts that do
        // not care keep writing new ChassisIdentity(id, source, baseName).
        var id = new ChassisIdentity("c", "MyConnectorService", "c");
        Assert.Equal("MyConnectorService", id.ServiceName);
    }

    [Fact]
    public void ServiceName_IsIndependentOfTheEventLogSourceWhenSet()
    {
        // Salesforce is the case: its Event Log source and its SCM service name
        // are not the same string.
        var id = new ChassisIdentity("salesforce_connector", "SalesforceConnector", "salesforce_connector")
        {
            ServiceName = "SalesforceCopilotConnector",
        };
        Assert.Equal("SalesforceCopilotConnector", id.ServiceName);
        Assert.Equal("SalesforceConnector", id.EventLogSource);
    }

    [Theory]
    [InlineData("SFCONNECTOR_HOME")]
    [InlineData("CLARIZEN_CONNECTOR_HOME")]
    [InlineData("HADOOP_CONNECTOR_HOME")]
    [InlineData("ALTRATA_CONNECTOR_HOME")]
    [InlineData("SEISMIC_CONNECTOR_HOME")]
    public void HomeEnvVar_CarriesEachConnectorsOwnSpelling(string spelling)
    {
        // These names are in deployed service definitions and operator runbooks.
        // The chassis takes the name as identity rather than imposing one,
        // because renaming would break every existing installation.
        var id = new ChassisIdentity("c", "S", "c") { HomeEnvVar = spelling };
        Assert.Equal(spelling, id.HomeEnvVar);
    }

    [Fact]
    public void HomeEnvVar_IsNotSeismicsByDefault()
    {
        // The regression guard for the actual defect: the chassis ServiceHost
        // used to hardcode SEISMIC_CONNECTOR_HOME, so a connector that adopted
        // it without setting HomeEnvVar would look for the wrong variable.
        Assert.NotEqual("SEISMIC_CONNECTOR_HOME", new ChassisIdentity("c", "S", "c").HomeEnvVar);
    }

    [Fact]
    public void Identity_RoundTripsThroughInit()
    {
        var id = new ChassisIdentity("hadoop_connector", "HadoopConnector", "hadoop_connector")
        {
            ServiceName = "HadoopConnector",
            HomeEnvVar = "HADOOP_CONNECTOR_HOME",
        };
        Chassis.Init(id);
        Assert.Equal("HADOOP_CONNECTOR_HOME", Chassis.Identity.HomeEnvVar);
        Assert.Equal("HadoopConnector", Chassis.Identity.ServiceName);
        Assert.Equal("hadoop_connector.service", Chassis.Identity.ServiceLoggerName);
    }

    // ── hooks ────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryHookIsNullByDefault()
    {
        // Salesforce and Hadoop rely on this: no hook, no Event Log entry.
        ServiceHost.ResetHooksForTests();
        Assert.Null(ServiceHost.OnStarting);
        Assert.Null(ServiceHost.OnStopRequested);
        Assert.Null(ServiceHost.OnFinished);
        Assert.Null(ServiceHost.OnStopped);
    }

    [Fact]
    public void OnFinishedAndOnStopped_AreDistinctHooks()
    {
        // They are separate because the copies genuinely disagreed: Seismic's
        // "Service stopped" was emitted in the worker's finally and so survived
        // an unhandled exception, while Clarizen's and Altrata's "finished"
        // events were emitted inside the try and did not. Collapsing them would
        // have changed one connector's Event Log on its failure path.
        var finished = new List<int>();
        var stopped = new List<int>();
        ServiceHost.OnFinished = finished.Add;
        ServiceHost.OnStopped = stopped.Add;

        ServiceHost.OnFinished?.Invoke(0);
        ServiceHost.OnStopped?.Invoke(0);
        ServiceHost.OnStopped?.Invoke(1);   // the crash path reaches only OnStopped

        Assert.Equal(new[] { 0 }, finished);
        Assert.Equal(new[] { 0, 1 }, stopped);
    }

    [Fact]
    public void OnStarting_ReceivesTheArgumentsAndTheWorkingDirectory()
    {
        // Clarizen and Seismic both put the working directory in their Event Log
        // start line, so it has to reach the hook rather than being logged only
        // by the chassis.
        (string[] Args, string Wd)? seen = null;
        ServiceHost.OnStarting = (args, wd) => seen = (args, wd);

        ServiceHost.OnStarting?.Invoke(["full-deployment", "--continuous"], @"C:\Connector");

        Assert.NotNull(seen);
        Assert.Equal(["full-deployment", "--continuous"], seen!.Value.Args);
        Assert.Equal(@"C:\Connector", seen!.Value.Wd);
    }

    [Fact]
    public void ResetHooksForTests_ClearsAllFour()
    {
        ServiceHost.OnStarting = (_, _) => { };
        ServiceHost.OnStopRequested = () => { };
        ServiceHost.OnFinished = _ => { };
        ServiceHost.OnStopped = _ => { };

        ServiceHost.ResetHooksForTests();

        Assert.Null(ServiceHost.OnStarting);
        Assert.Null(ServiceHost.OnStopRequested);
        Assert.Null(ServiceHost.OnFinished);
        Assert.Null(ServiceHost.OnStopped);
    }
}
