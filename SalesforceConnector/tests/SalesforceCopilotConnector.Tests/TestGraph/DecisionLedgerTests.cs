// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// DecisionLedgerTests.cs — #11 immutable, hash-chained decision audit ledger.
// Verifies the chain links entry-to-entry and that Verify() detects tamper
// (edited field, deleted line, reordering) and structural corruption.

using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

[Collection("EnvVars")]  // CreateIfEnabledRespectsEnvFlag mutates DECISION_LEDGER
public sealed class DecisionLedgerTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("decision_ledger_").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private DecisionLedger NewLedger() => new("test-connector", _tmp);

    [Fact]
    public void ChainLinksEntryToEntry()
    {
        var ledger = NewLedger();
        var e1 = ledger.Append(DecisionKinds.AclRestriction, "001", "Account", "classification=Restricted");
        var e2 = ledger.Append(DecisionKinds.Exclusion, "002", "Case", "scale guard fallback");
        var e3 = ledger.Append(DecisionKinds.AclRestriction, "003", "Lead", "scale guard group");

        Assert.Equal(1, e1.Seq);
        Assert.Equal(2, e2.Seq);
        Assert.Equal(3, e3.Seq);

        // Genesis links to all-zero; each subsequent PrevHash is the prior Hash.
        Assert.Equal(DecisionLedger.GenesisHash, e1.PrevHash);
        Assert.Equal(e1.Hash, e2.PrevHash);
        Assert.Equal(e2.Hash, e3.PrevHash);

        Assert.True(ledger.Verify(out var broken));
        Assert.Equal(0, broken);

        var all = ledger.ReadAll();
        Assert.Equal(3, all.Count);
        Assert.Equal("001", all[0].ItemId);
        Assert.Equal(DecisionKinds.Exclusion, all[1].Decision);
    }

    [Fact]
    public void VerifyPassesForFreshAndEmptyLedger()
    {
        var ledger = NewLedger();
        Assert.True(ledger.Verify(out var broken));   // no file yet
        Assert.Equal(0, broken);
    }

    [Fact]
    public void VerifyDetectsEditedField()
    {
        var ledger = NewLedger();
        ledger.Append(DecisionKinds.AclRestriction, "001", "Account", "reason-one");
        ledger.Append(DecisionKinds.AclRestriction, "002", "Account", "reason-two");

        // Tamper: change a field's value on line 1 without recomputing the hash.
        var lines = File.ReadAllLines(ledger.Path);
        lines[0] = lines[0].Replace("reason-one", "reason-EDITED");
        File.WriteAllLines(ledger.Path, lines);

        // A fresh instance so no cached tail masks the tamper.
        Assert.False(NewLedger().Verify(out var broken));
        Assert.Equal(1, broken);
    }

    [Fact]
    public void VerifyDetectsDeletedEntry()
    {
        var ledger = NewLedger();
        ledger.Append(DecisionKinds.AclRestriction, "001", "Account", "r1");
        ledger.Append(DecisionKinds.Exclusion, "002", "Case", "r2");
        ledger.Append(DecisionKinds.AclRestriction, "003", "Lead", "r3");

        // Delete the middle entry — the chain link from 3→(was 2) now breaks.
        var lines = File.ReadAllLines(ledger.Path).ToList();
        lines.RemoveAt(1);
        File.WriteAllLines(ledger.Path, lines);

        Assert.False(NewLedger().Verify(out var broken));
        Assert.NotEqual(0, broken);
    }

    [Fact]
    public void VerifyDetectsStructuralCorruption()
    {
        var ledger = NewLedger();
        ledger.Append(DecisionKinds.AclRestriction, "001", "Account", "r1");

        // Append a garbage (non-JSON) line.
        File.AppendAllText(ledger.Path, "{ this is not valid json\n");

        Assert.False(NewLedger().Verify(out var broken));
        Assert.Equal(2, broken);   // line 2 does not parse
    }

    [Fact]
    public void AppendRefusesToExtendACorruptChain()
    {
        var ledger = NewLedger();
        ledger.Append(DecisionKinds.AclRestriction, "001", "Account", "r1");
        File.AppendAllText(ledger.Path, "corrupt line\n");

        // A fresh instance re-reads the file on append and must refuse.
        Assert.Throws<InvalidDataException>(() =>
            NewLedger().Append(DecisionKinds.Exclusion, "002", "Case", "r2"));
    }

    [Fact]
    public void CreateIfEnabledRespectsEnvFlag()
    {
        var saved = Environment.GetEnvironmentVariable(DecisionLedger.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, null);
            Assert.Null(DecisionLedger.CreateIfEnabled("c", _tmp));
            Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, "true");
            Assert.NotNull(DecisionLedger.CreateIfEnabled("c", _tmp));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DecisionLedger.EnvVar, saved);
        }
    }
}
