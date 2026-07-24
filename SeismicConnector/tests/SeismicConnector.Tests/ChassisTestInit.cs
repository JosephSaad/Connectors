// ChassisTestInit.cs
// ------------------
// The test assembly does not run Program.Main, so it installs the connector's
// chassis identity itself — the exact strings Program.Main installs in
// production — before any test touches a chassis type. This keeps parameterised
// logger names ("seismic_connector.*"), the Event Log source ("SeismicConnector"),
// and the JSON-log correlation-id wiring byte-identical to the pre-extraction
// behaviour the suite asserts on. A [ModuleInitializer] runs once, ahead of any
// [Fact], when the test assembly is loaded.

using System.Runtime.CompilerServices;
using Connector.Chassis;
using SeismicConnector.Infrastructure;

namespace SeismicConnector.Tests;

internal static class ChassisTestInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        Chassis.Init(new ChassisIdentity("seismic_connector", "SeismicConnector", "seismic_connector"));
        Chassis.CorrelationIdProvider = () => Tracing.CurrentCorrelationId;
    }
}
