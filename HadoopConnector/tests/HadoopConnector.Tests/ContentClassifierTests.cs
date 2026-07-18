// Dependency-free content classifier: per-category detection, Luhn true/false,
// no false-positive on ordinary numbers, config loading. No network.

using HadoopConnector.Content;

namespace HadoopConnector.Tests;

public class ContentClassifierTests
{
    private static ContentClassifier Repo() =>
        ContentClassifier.Load(Path.Combine(AppContext.BaseDirectory, "config", "classification.json"));

    // ── PII ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Detects_Email()
    {
        Assert.Contains("PII", Repo().Detect("contact me at gopi.raman@example.co.uk please"));
    }

    [Fact]
    public void Detects_UsSsn()
    {
        Assert.Contains("PII", Repo().Detect("SSN 123-45-6789 on file"));
    }

    [Fact]
    public void Detects_Phone()
    {
        Assert.Contains("PII", Repo().Detect("call +44 20 7946 0958 tomorrow"));
    }

    // ── PCI (Luhn) ───────────────────────────────────────────────────────────

    [Fact]
    public void Detects_ValidCard_ByLuhn()
    {
        // 4111 1111 1111 1111 is a well-known Luhn-valid test Visa number.
        Assert.Contains("PCI", Repo().Detect("card 4111 1111 1111 1111 exp 12/29"));
    }

    [Fact]
    public void DoesNotDetect_LuhnInvalidCardShapedNumber()
    {
        // Same length/shape but fails the checksum → not PCI.
        var found = Repo().Detect("ref 4111 1111 1111 1112");
        Assert.DoesNotContain("PCI", found);
    }

    [Fact]
    public void DoesNotDetect_OrdinaryLongNumbers_AsPci()
    {
        // Budget figures / ids are not cards.
        Assert.DoesNotContain("PCI", Repo().Detect("planned cost 120000 and id 900012345"));
        Assert.DoesNotContain("PCI", Repo().Detect("order 1234567890123 total"));
    }

    [Fact]
    public void Luhn_TrueAndFalse_Directly()
    {
        Assert.True(ContentClassifier.IsLuhnValid("4111111111111111"));
        Assert.True(ContentClassifier.IsLuhnValid("4111 1111 1111 1111"));
        Assert.False(ContentClassifier.IsLuhnValid("4111111111111112"));
        Assert.False(ContentClassifier.IsLuhnValid("123"));            // too short
        Assert.False(ContentClassifier.IsLuhnValid("12345678901234567890")); // too long
        Assert.False(ContentClassifier.IsLuhnValid("4111-1111-abcd"));  // non-digit
    }

    // ── Secret ───────────────────────────────────────────────────────────────

    [Fact]
    public void Detects_AwsAccessKey()
    {
        Assert.Contains("Secret", Repo().Detect("key AKIAIOSFODNN7EXAMPLE here"));
    }

    [Fact]
    public void Detects_PrivateKeyBlock()
    {
        Assert.Contains("Secret", Repo().Detect("-----BEGIN RSA PRIVATE KEY-----\nMIIE..."));
    }

    [Fact]
    public void Detects_BearerToken()
    {
        Assert.Contains("Secret", Repo().Detect("Authorization: Bearer abcdef0123456789abcdef01"));
    }

    // ── Negatives / robustness ───────────────────────────────────────────────

    [Fact]
    public void CleanText_DetectsNothing()
    {
        Assert.Empty(Repo().Detect("Migrate the tenant and verify the phase-one milestone."));
        Assert.Empty(Repo().Detect(""));
        Assert.Empty(Repo().Detect(null));
        Assert.Empty(Repo().Detect("   "));
    }

    [Fact]
    public void MultipleCategories_AllDetected()
    {
        var found = Repo().Detect("email a@b.com card 4111111111111111 key AKIAIOSFODNN7EXAMPLE");
        Assert.Contains("PII", found);
        Assert.Contains("PCI", found);
        Assert.Contains("Secret", found);
    }

    [Fact]
    public void FromJson_InvalidRegex_IsSkipped_NotCrashed()
    {
        var classifier = ContentClassifier.FromJson("""
            {"categories": [
                {"name": "Bad", "patterns": [{"name":"x","regex":"[unterminated"}]},
                {"name": "PII", "patterns": [{"name":"email","regex":"\\S+@\\S+\\.\\S+"}]}
            ]}
            """);
        Assert.Single(classifier.Categories);   // Bad dropped (all patterns invalid)
        Assert.Contains("PII", classifier.Detect("a@b.com"));
    }

    [Fact]
    public void FromJson_LuhnFlag_Respected()
    {
        var classifier = ContentClassifier.FromJson("""
            {"categories": [
                {"name": "PCI", "luhn": true, "patterns": [{"name":"c","regex":"\\b\\d{13,19}\\b"}]}
            ]}
            """);
        Assert.Contains("PCI", classifier.Detect("4111111111111111"));
        Assert.DoesNotContain("PCI", classifier.Detect("4111111111111112"));
    }

    // ── ReDoS resilience (bounded match timeout) ─────────────────────────────

    [Fact]
    public void PathologicalPattern_TimesOut_InsteadOfHanging()
    {
        // (a+)+$ against a long run of 'a' with a non-matching tail is the
        // classic catastrophic-backtracking pair (exponential without a
        // timeout). Detect must finish quickly — the timeout turns the
        // pathological pattern into a no-match instead of hanging the crawl.
        var classifier = ContentClassifier.FromJson("""
            {"categories": [
                {"name": "Evil", "patterns": [{"name":"redos","regex":"(a+)+$"}]},
                {"name": "PII", "patterns": [{"name":"email","regex":"\\S+@\\S+\\.\\S+"}]}
            ]}
            """);
        var input = new string('a', 10_000) + "! a@b.com " + new string('a', 10_000) + "!";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = classifier.Detect(input);
        sw.Stop();

        Assert.DoesNotContain("Evil", found);  // timed out → treated as no-match
        Assert.Contains("PII", found);         // other categories still evaluated
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(30),
            $"Detect took {sw.Elapsed} — the match timeout did not engage");
    }

    [Fact]
    public void PathologicalLuhnPattern_TimesOut_InsteadOfHanging()
    {
        var classifier = ContentClassifier.FromJson("""
            {"categories": [
                {"name": "PCI", "luhn": true, "patterns": [{"name":"redos","regex":"(\\d+)+$"}]}
            ]}
            """);
        var input = new string('1', 10_000) + "x";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = classifier.Detect(input);
        sw.Stop();

        Assert.Empty(found);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(30),
            $"Detect took {sw.Elapsed} — the match timeout did not engage");
    }
}
