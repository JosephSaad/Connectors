// Test bootstrap for the R2 logging unification.
// ----------------------------------------------
// Logging now lives in Connector.Chassis and cannot reference ClarizenConnector
// types, so — exactly as the production host does in Program.WireChassisLogging —
// the Clarizen test assembly wires the chassis logging hooks and selects Clarizen
// mode via a [ModuleInitializer]. This runs once when the test assembly loads,
// before any test, so every test (including the many that never call
// Logging.Initialize but still assert on the Clarizen text/json format, TestSink,
// the Event Log mirror, or correlation-id stamping) runs in Clarizen mode with:
//   • Chassis.CorrelationIdProvider  → CorrelationContext.Current (correlation)
//   • Logging.HardenDirectoryHook    → DirectoryHardening.EnsureOwnerOnly
//   • Logging.EventLogMirrorHook     → EventLogSink.Mirror
//   • Logging.UseStandardMode()      → Clarizen text/json format + run-dir sink
//
// Logging.ResetForTests() (called by many test fixtures) deliberately leaves the
// mode, hooks and correlation provider intact, so this wiring survives resets.

using System.Runtime.CompilerServices;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Tests;

internal static class TestLoggingModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        Connector.Chassis.Chassis.Init(
            new Connector.Chassis.ChassisIdentity(
                "clarizen_connector", EventLogSink.Source, "clarizen_connector"));
        Connector.Chassis.Chassis.CorrelationIdProvider = () => CorrelationContext.Current;
        Logging.HardenDirectoryHook = DirectoryHardening.EnsureOwnerOnly;
        Logging.EventLogMirrorHook = EventLogSink.Mirror;
        Logging.UseStandardMode();
    }
}
