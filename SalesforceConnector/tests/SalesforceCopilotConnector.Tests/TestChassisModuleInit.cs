// Test bootstrap for the shared chassis.
// --------------------------------------
// Program.Main calls WireChassis() in the host; tests never go through Main, so
// the same wiring runs here from a [ModuleInitializer]. It executes once when
// the test assembly loads, before any test, which matters because chassis
// components name their loggers from Chassis.Identity at type-load — wiring
// later would be silently ignored.
//
// The logger factory routes chassis log lines back into this connector's own
// handler-based logging rather than the chassis's sinks.

using System.Runtime.CompilerServices;
using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests;

internal static class TestChassisModuleInit
{
    [ModuleInitializer]
    internal static void Init() => Program.WireChassis();
}
