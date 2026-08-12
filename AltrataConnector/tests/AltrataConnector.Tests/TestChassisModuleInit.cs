// Test bootstrap for the shared chassis.
// --------------------------------------
// Program.Main calls WireChassis() in the host; tests never go through Main, so
// the same wiring runs here from a [ModuleInitializer]. It executes once when the
// assembly loads, before any test, which matters because chassis components name
// their loggers from Chassis.Identity at type-load — wiring later is ignored.

using System.Runtime.CompilerServices;

namespace AltrataConnector.Tests;

internal static class TestChassisModuleInit
{
    [ModuleInitializer]
    internal static void Init() => Program.WireChassis();
}
