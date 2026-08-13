// LedgerNamingTests.cs
// --------------------
// The chassis log pruner refuses to delete a run directory that holds a decision
// ledger. That guard is a glob, so it protects exactly the filenames it happens
// to match — and this fleet writes two:
//
//   Seismic, Salesforce, Altrata   decision_ledger_<id>.jsonl
//   Clarizen, Hadoop               decisions_<id>.jsonl
//
// A guard listing only the first scans, finds nothing, and deletes a directory
// holding the second. It fails OPEN, and it fails silently: the code reads as
// protective, the comment says it is protective, and the audit records are gone.
// Nothing else in the suite would notice, because a pruner that deletes is
// working as designed.
//
// So the naming convention is asserted directly against the source that builds
// the paths. Renaming a ledger, or adding a connector whose name the chassis
// list does not cover, fails here rather than years later during an audit.

using System.Text.RegularExpressions;

namespace ClarizenConnector.Tests;

public class LedgerNamingTests
{
    /// <summary>Every connector, and the literal its ledger path is built from.</summary>
    public static TheoryData<string, string> LedgerPathSources() => new()
    {
        { "ClarizenConnector",   "ClarizenConnector/src/ClarizenConnector/Infrastructure/DecisionLedger.cs" },
        { "HadoopConnector",     "HadoopConnector/src/HadoopConnector/Infrastructure/DecisionLedger.cs" },
        { "SalesforceConnector", "SalesforceConnector/src/SalesforceCopilotConnector/Graph/DecisionLedger.cs" },
        { "AltrataConnector",    "AltrataConnector/src/AltrataConnector/Altrata/DecisionLedger.cs" },
        // Seismic has no local ledger — it consumes the chassis one, so the chassis
        // file IS its ledger source. Listed explicitly rather than omitted, so the
        // guard is asserted against every ledger the fleet actually writes.
        { "Connector.Chassis",   "Connector.Chassis/DecisionLedger.cs" },
    };

    [Theory]
    [MemberData(nameof(LedgerPathSources))]
    public void EveryConnectorLedgerFilenameIsCoveredByTheChassisPrunerGuard(string connector, string relativePath)
    {
        var full = Path.Combine(RepoRoot(), relativePath);
        Skip_IfSourceMissing(full, connector);

        var source = File.ReadAllText(full);

        // The interpolated filename literal, e.g. $"decisions_{connectorId}.jsonl".
        var literal = Regex.Match(source, @"\$""(?<name>[A-Za-z_]+)\{[A-Za-z]+\}\.jsonl""");
        Assert.True(literal.Success,
            $"{connector}: could not find the ledger filename literal in {relativePath}. If the path is now "
            + "built differently, update this test — do not delete it; it is what keeps the pruner's guard honest.");

        var prefix = literal.Groups["name"].Value;          // e.g. "decisions_"
        var example = $"{prefix}example.jsonl";

        Assert.True(
            Connector.Chassis.LogPruner.LedgerFilePatterns.Any(p => MatchesGlob(example, p)),
            $"{connector} writes its decision ledger as '{example}', which NONE of the chassis pruner's "
            + $"guard patterns match ({string.Join(", ", Connector.Chassis.LogPruner.LedgerFilePatterns)}). The pruner would "
            + "scan that directory, find no ledger, and delete it along with the audit records. Add the "
            + "pattern to Connector.Chassis.LogPruner.LedgerFilePatterns, or rename the ledger to an already-covered form.");
    }

    /// <summary>Both fleet conventions are covered, asserted directly rather than inferred.</summary>
    [Theory]
    [InlineData("decision_ledger_clarizen.jsonl")]
    [InlineData("decisions_clarizen.jsonl")]
    public void BothFleetNamingConventionsAreGuarded(string filename)
    {
        Assert.True(Connector.Chassis.LogPruner.LedgerFilePatterns.Any(p => MatchesGlob(filename, p)),
            $"'{filename}' is a ledger name in use in this fleet but no guard pattern matches it.");
    }

    /// <summary>A name that is not a ledger must NOT be matched, or the guard blocks all pruning.</summary>
    [Theory]
    [InlineData("connector.log")]
    [InlineData("checkpoint_clarizen.json")]
    [InlineData("failed_records_clarizen.jsonl")]
    public void NonLedgerFilesAreNotTreatedAsLedgers(string filename)
    {
        Assert.False(Connector.Chassis.LogPruner.LedgerFilePatterns.Any(p => MatchesGlob(filename, p)),
            $"'{filename}' is not a decision ledger, but a guard pattern matches it — retention would "
            + "never delete anything.");
    }

    private static bool MatchesGlob(string name, string glob) =>
        Regex.IsMatch(name, "^" + Regex.Escape(glob).Replace("\\*", ".*") + "$");

    private static void Skip_IfSourceMissing(string full, string connector) =>
        Assert.True(File.Exists(full), $"{connector}: expected ledger source at {full}");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Connector.Chassis")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
