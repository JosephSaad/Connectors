// Test bootstrap for the shared-chassis logging (re-platform onto Connector.Chassis).
// -----------------------------------------------------------------------------------
// Logging now lives in Connector.Chassis and cannot reference HadoopConnector types,
// so — exactly as Program.WireChassisLogging does in the host — the test assembly
// wires the chassis logging hooks and selects the standard (run-directory) format via
// a [ModuleInitializer]. This runs once when the test assembly loads, before any test,
// so every test (including those that never call Logging.Initialize but still assert on
// the text/json format, TestSink, the Event Log mirror, or correlation-id stamping)
// runs with:
//   • Chassis.CorrelationIdProvider  → CorrelationContext.Current
//   • Logging.HardenDirectoryHook    → SecureDirectories.CreateHardened
//   • Logging.EventLogMirrorHook     → EventLogSink.Mirror
//   • Logging.UseStandardMode()      → standard text/json format + run-dir sink
//
// Logging.ResetForTests() (called by test fixtures) leaves the mode, hooks and
// correlation provider intact, so this wiring survives resets.

using System.Runtime.CompilerServices;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

internal static class TestChassisModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        Connector.Chassis.Chassis.Init(
            new Connector.Chassis.ChassisIdentity(
                "hadoop_connector", EventLogSink.SourceName, "hadoop_connector"));
        Connector.Chassis.Chassis.CorrelationIdProvider = () => CorrelationContext.Current;
        Logging.HardenDirectoryHook = SecureDirectories.CreateHardened;
        Logging.EventLogMirrorHook = EventLogSink.Mirror;
        Logging.UseStandardMode();
    }
}
