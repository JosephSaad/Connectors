// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Stress-test harness for the ingestion pipeline.
//
// Runs the REAL Ingest.IngestContentAsync end-to-end with fake Graph/Salesforce
// layers injected at the internal test seams (Ingest.*Hook, SyncState.LogsDir,
// GraphClient.BatchRequestsAsync), exactly like the unit tests do — but at
// stress volumes, with simulated latency, 429 bursts, permanent 500s and a
// graceful stop/resume cycle.
//
// Usage:
//   dotnet run -c Release --project tools/StressHarness -- \
//       [--items N] [--scenario all|throughput|throttle|failures|stop-resume] \
//       [--latency-ms M] [--workdir DIR]

using System.Globalization;

namespace StressHarness;

public static class Program
{
    private static readonly string[] AllScenarios =
        { "throughput", "throttle", "failures", "stop-resume" };

    public static async Task<int> Main(string[] args)
    {
        int? items = null;
        int? latencyMs = null;
        var scenario = "all";
        var workdir = "stress-out";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--items":
                    items = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--scenario":
                    scenario = args[++i];
                    break;
                case "--latency-ms":
                    latencyMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--workdir":
                    workdir = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }

        if (scenario != "all" && !AllScenarios.Contains(scenario))
        {
            Console.Error.WriteLine(
                $"Unknown scenario '{scenario}' — expected all|{string.Join('|', AllScenarios)}");
            return 2;
        }

        // Resolve the workdir before any cwd change.
        workdir = Path.GetFullPath(workdir);
        Directory.CreateDirectory(workdir);

        // Settings.RepoRoot is CWD-based (config/schema.json feeds the pipeline's
        // static object-type list). When launched away from the repo root, fall
        // back to the config copy in the harness output directory.
        if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "config", "schema.json")))
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "config", "schema.json")))
            {
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            }
            else
            {
                Console.Error.WriteLine(
                    "config/schema.json not found — run the harness from the repo root.");
                return 2;
            }
        }

        Console.WriteLine($"StressHarness — workdir: {workdir}");
        Console.WriteLine($"Scenario(s): {scenario}, items override: {(items?.ToString() ?? "per-scenario default")}, " +
                          $"latency override: {(latencyMs?.ToString() ?? "per-scenario default")} ms");

        var runner = new ScenarioRunner(workdir, latencyMs);
        var toRun = scenario == "all" ? AllScenarios : new[] { scenario };
        var results = new List<ScenarioResult>();

        foreach (var name in toRun)
        {
            Console.WriteLine($"\n──── scenario: {name} ────");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = name switch
            {
                "throughput" => await runner.RunThroughputAsync(items ?? 100_000),
                "throttle" => await runner.RunThrottleAsync(items ?? 50_000),
                "failures" => await runner.RunFailuresAsync(items ?? 20_000),
                "stop-resume" => await runner.RunStopResumeAsync(items ?? 50_000),
                _ => throw new InvalidOperationException(name),
            };
            sw.Stop();
            results.Add(result);

            foreach (var note in result.Notes)
                Console.WriteLine($"  note : {note}");
            foreach (var inv in result.PassedInvariants)
                Console.WriteLine($"  PASS : {inv}");
            foreach (var inv in result.FailedInvariants)
                Console.WriteLine($"  FAIL : {inv}");
            Console.WriteLine($"  {result.Name}: {(result.Pass ? "PASS" : "FAIL")} " +
                              $"({sw.Elapsed.TotalSeconds:F1}s total incl. verification)");
        }

        PrintSummary(results);
        return results.All(r => r.Pass) ? 0 : 1;
    }

    private static void PrintSummary(List<ScenarioResult> results)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 88));
        Console.WriteLine(" STRESS HARNESS SUMMARY");
        Console.WriteLine(new string('=', 88));
        Console.WriteLine(
            $" {"Scenario",-12} {"Items",8} {"Wall(s)",9} {"Items/s",9} {"PeakRSS(MB)",12} {"Alloc(MB)",10} {"Outcome",8}");
        Console.WriteLine(new string('-', 88));
        foreach (var r in results)
        {
            Console.WriteLine(
                $" {r.Name,-12} {r.Items,8} {r.WallSecs,9:F1} {r.ItemsPerSec,9:F0} " +
                $"{r.PeakRssMb,12:F0} {r.AllocMb,10:F0} {(r.Pass ? "PASS" : "FAIL"),8}");
        }
        Console.WriteLine(new string('-', 88));
        var passed = results.Count(r => r.Pass);
        var invPassed = results.Sum(r => r.PassedInvariants.Count);
        var invFailed = results.Sum(r => r.FailedInvariants.Count);
        Console.WriteLine(
            $" Scenarios: {passed}/{results.Count} passed. Invariants: {invPassed} passed, {invFailed} failed.");
        foreach (var r in results.Where(r => !r.Pass))
        {
            foreach (var inv in r.FailedInvariants)
                Console.WriteLine($"   FAIL [{r.Name}] {inv}");
        }
        Console.WriteLine(new string('=', 88));
    }
}
