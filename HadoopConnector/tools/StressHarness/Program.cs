// StressHarness — heavy-scale stress driver for the BDH Hadoop Copilot connector.
//
// Drives the REAL pipeline components (partition scanner / filter engine / fetcher,
// ingest pipeline / Graph $batch / adaptive concurrency, circuit breaker, sync
// state / checkpoint / dead-letter) with in-harness fakes at 10^5–10^6 volume and
// prints measured numbers + invariant checks. No real network, no config assets.
//
// Usage:
//   export PATH="$HOME/.dotnet:$PATH"
//   dotnet run -c Release --project tools/StressHarness -- [--scenario NAME] [--scale N] [--workdir DIR]
//
//   --scenario  all | filter-scale | memory-bounds | fail-closed | dead-letter |
//               circuit-breaker | checkpoint-resume | batch-429 | oversize-sweep |
//               guard-matrix | open-retry | identity-churn | watermark-storm |
//               enterprise-concurrency
//               (default: all)
//   --scale     integer volume multiplier (default 1 ≈ 1M-row filter scan,
//               2M-row memory stream, 20k-item ingest). Use 3–5 for a heavier soak.
//   --workdir   scratch dir for state/inventory (default: ./stress-out)

using System.Globalization;
using StressHarness;

var scenario = "all";
var scale = 1;
var workdir = "stress-out";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--scenario": scenario = args[++i]; break;
        case "--scale": scale = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--workdir": workdir = args[++i]; break;
        default: Console.Error.WriteLine($"Unknown arg: {args[i]}"); return 2;
    }
}

workdir = Path.GetFullPath(workdir);
Directory.CreateDirectory(workdir);
scale = Math.Max(1, scale);

var all = new (string Name, Func<ScenarioRunner, Task<ScenarioResult>> Run)[]
{
    ("filter-scale", r => r.FilterAtScaleAsync()),
    ("memory-bounds", r => r.MemoryBoundsAsync()),
    ("fail-closed", r => r.FailClosedAsync()),
    ("dead-letter", r => r.DeadLetterConcurrencyAsync()),
    ("circuit-breaker", r => r.CircuitBreakerAsync()),
    ("checkpoint-resume", r => r.CheckpointResumeAsync()),
    ("batch-429", r => r.BatchThroughputAsync()),
    // Round 2 — post-code-review-fix scenarios.
    ("oversize-sweep", r => r.OversizeSweepAsync()),
    ("guard-matrix", r => r.GuardMatrixAsync()),
    ("open-retry", r => r.OpenRetryAsync()),
    ("identity-churn", r => r.IdentityChurnAsync()),
    ("watermark-storm", r => r.WatermarkStormAsync()),
    // Round 3 — enterprise pack (cert auth / CA transport / event log / redaction
    // / metrics) under concurrency, plus a WebHDFS delegation-token log canary.
    ("enterprise-concurrency", r => r.EnterpriseConcurrencyAsync()),
};

if (scenario != "all" && all.All(s => s.Name != scenario))
{
    Console.Error.WriteLine($"Unknown scenario '{scenario}'. Expected: all | {string.Join(" | ", all.Select(s => s.Name))}");
    return 2;
}

Console.WriteLine($"StressHarness — scale={scale}, workdir={workdir}");
var runner = new ScenarioRunner(workdir, scale);
var toRun = scenario == "all" ? all : all.Where(s => s.Name == scenario).ToArray();
var results = new List<ScenarioResult>();

foreach (var (name, run) in toRun)
{
    Console.WriteLine($"\n──── {name} ────");
    ScenarioResult result;
    try
    {
        result = await run(runner);
    }
    catch (Exception ex)
    {
        result = new ScenarioResult { Name = name };
        result.Failed.Add($"threw: {ex.GetType().Name}: {ex.Message}");
    }
    results.Add(result);
    foreach (var m in result.Metrics) Console.WriteLine($"  · {m}");
    foreach (var p in result.Passed) Console.WriteLine($"  PASS {p}");
    foreach (var f in result.Failed) Console.WriteLine($"  FAIL {f}");
    Console.WriteLine($"  → {name}: {(result.Pass ? "PASS" : "FAIL")} in {result.WallSecs:F2}s"
                      + (result.PeakRssMb > 0 ? $", peak RSS {result.PeakRssMb:N0} MB" : ""));
}

Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine(" STRESS HARNESS SUMMARY");
Console.WriteLine(new string('=', 78));
foreach (var r in results)
    Console.WriteLine($"  {(r.Pass ? "PASS" : "FAIL")}  {r.Name,-18} {r.WallSecs,7:F2}s"
                      + $"  ({r.Passed.Count} invariants ok{(r.Failed.Count > 0 ? $", {r.Failed.Count} FAILED" : "")})");
Console.WriteLine(new string('=', 78));
var passed = results.Count(r => r.Pass);
Console.WriteLine($" {passed}/{results.Count} scenarios passed, "
                  + $"{results.Sum(r => r.Passed.Count)} invariants held"
                  + $"{(results.Sum(r => r.Failed.Count) is var nf && nf > 0 ? $", {nf} broke" : "")}.");
return results.All(r => r.Pass) ? 0 : 1;
