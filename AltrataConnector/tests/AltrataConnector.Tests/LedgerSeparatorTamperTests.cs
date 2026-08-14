// LedgerSeparatorTamperTests.cs
// -----------------------------
// A tamper-evident ledger has exactly one job: no single-byte edit of the file
// may go unnoticed. One class of edit did.
//
// HashChainedLedger read with StreamReader.ReadLine, which breaks a line on
// '\r', '\n' and "\r\n" alike. Append only ever writes '\n', so overwriting a
// separator LF with a CR left the two records parsing exactly as before — and
// the hash chain only ever sees parsed records, so Verify() returned TRUE over
// a file whose bytes had changed. Reproduced against the shipped build before
// the fix; the byte the CI failure named was pos=274261 0x0A→0x0D.
//
// It also made LedgerScaleTamperStressTests intermittent rather than reliably
// red: that test probes 500 positions chosen from rng.Next(pristine.Length), so
// a one-byte change in ledger size reshuffles every probe and whether any lands
// on a separator is a coin flip. It passed in #53 and #55 and failed in #56.
//
// These tests assert on BYTES rather than on parses, because the whole defect
// was that the parse was unchanged.

using System.Text;
using AltrataConnector.Altrata;

namespace AltrataConnector.Tests;

public class LedgerSeparatorTamperTests
{
    private static ErasureLedger Seeded(out string root, int entries = 12)
    {
        root = TestFixtures.NewTempDir("ledgersep");
        var ledger = new ErasureLedger("SeparatorProbe", logsDir: root);
        for (var i = 0; i < entries; i++)
        {
            ledger.Append("joseph", ErasureActions.Erase, $"subject-{i}", null, new[] { $"item-{i}" });
        }
        Assert.True(ledger.Verify(out _), "seeded ledger should verify");
        return ledger;
    }

    [Fact]
    public void OverwritingASeparatorLfWithACr_IsDetected()
    {
        var ledger = Seeded(out _);
        var bytes = File.ReadAllBytes(ledger.Path);
        var separator = Array.IndexOf(bytes, (byte)'\n');
        Assert.True(separator > 0, "the ledger should be newline-delimited");

        bytes[separator] = (byte)'\r';        // exactly the CI failure: 0x0A -> 0x0D
        File.WriteAllBytes(ledger.Path, bytes);

        Assert.False(
            ledger.Verify(out var brokenAt),
            "a single-byte edit of the record separator went UNDETECTED — the file's bytes "
            + "changed and the ledger still called itself intact, which is the one guarantee "
            + "a tamper-evident ledger makes.");
        Assert.True(brokenAt > 0);
    }

    [Fact]
    public void EveryLfInTheFileIsProtected_NotJustTheFirst()
    {
        // The storm test only fails when a probe happens to land on a separator.
        // This walks all of them, so the guarantee does not depend on luck.
        var ledger = Seeded(out _);
        var pristine = File.ReadAllBytes(ledger.Path);

        var checkedCount = 0;
        for (var i = 0; i < pristine.Length; i++)
        {
            if (pristine[i] != (byte)'\n')
                continue;

            var tampered = (byte[])pristine.Clone();
            tampered[i] = (byte)'\r';
            File.WriteAllBytes(ledger.Path, tampered);
            Assert.False(ledger.Verify(out _), $"LF->CR at offset {i} went undetected");
            checkedCount++;
        }

        Assert.True(checkedCount >= 12, $"expected one separator per entry, walked {checkedCount}");
        File.WriteAllBytes(ledger.Path, pristine);
        Assert.True(ledger.Verify(out _), "restoring the pristine bytes should verify again");
    }

    [Fact]
    public void ACrlfLedgerFromTheOlderWriterStillVerifies()
    {
        // The fix must not turn a legacy file into a false tamper alarm. An
        // earlier build terminated records with Environment.NewLine, so ledgers
        // written on Windows before that was fixed are CRLF throughout. A
        // trailing CR is a terminator; only a CR somewhere else is damage.
        var ledger = Seeded(out _);
        var text = File.ReadAllText(ledger.Path, new UTF8Encoding(false));
        Assert.DoesNotContain("\r", text);

        File.WriteAllText(ledger.Path, text.Replace("\n", "\r\n"), new UTF8Encoding(false));
        Assert.Contains("\r\n", File.ReadAllText(ledger.Path, new UTF8Encoding(false)));

        Assert.True(
            ledger.Verify(out _),
            "a CRLF-terminated ledger from the older writer must still verify — the fix "
            + "distinguishes a CR that TERMINATES a record from one that REPLACED a terminator.");
    }

    [Fact]
    public void TheBrokenLineNumberStillMatchesTheFile()
    {
        // ReadLine produced no final empty line for the trailing '\n'; splitting
        // on '\n' does. If that element were not dropped, every reported line
        // number past the break would be right by luck and the last one wrong.
        var ledger = Seeded(out _, entries: 5);
        var lines = File.ReadAllLines(ledger.Path);
        Assert.Equal(5, lines.Length);

        var text = File.ReadAllText(ledger.Path, new UTF8Encoding(false));
        var third = text.Split('\n')[2];
        File.WriteAllText(
            ledger.Path,
            text.Replace(third, third[..^1] + "X"),   // corrupt line 3 only
            new UTF8Encoding(false));

        Assert.False(ledger.Verify(out var brokenAt));
        Assert.Equal(3, brokenAt);
    }
}
