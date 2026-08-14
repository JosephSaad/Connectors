// RedactionAndDialectTests.cs
// ---------------------------
// Two chassis policy surfaces that hosts and operators depend on by their
// OUTPUT, not their implementation:
//
//   LogRedaction      — the only thing standing between an email address and a
//                       retained, SIEM-shipped, un-erasable run log.
//   StandardLogDialect — the host-selectable format/gating policy the Standard
//                       sink consults. Every member is virtual precisely so a
//                       connector can keep the log contract its operators wrote
//                       queries and alert rules against.
//
// The dialect half is deliberately paranoid about ONE failure: a dialect whose
// overrides are silently ignored. That would compile, pass a unit test of the
// dialect in isolation, and quietly change every log line and Event Log entry
// of whichever connector wired it. So each seam is asserted through
// Logging.Write — the real sink — and not by calling the dialect directly.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Connector.Chassis.Tests;

/// <summary>
/// <see cref="LogRedaction.MaskEmail"/>: the shape, the stability and the
/// non-address paths.
///
/// This is PSEUDONYMISATION, not anonymisation, and these tests are written to
/// pin that distinction rather than paper over it: the domain survives in
/// clear, the hash is an unsalted SHA-256 of the whole address, and a holder of
/// a candidate user list reverses it with one dictionary pass (see
/// <see cref="MaskEmail_IsReversibleByDictionaryOverAKnownUserList"/>). The
/// output is therefore still personal data and still belongs inside the log's
/// retention and access controls. What it buys is that a leaked log is not a
/// harvestable address book.
/// </summary>
public class LogRedactionMaskEmailTests
{
    [Fact]
    public void MaskEmail_RendersSixteenHexAtTheDomain()
    {
        var masked = LogRedaction.MaskEmail("jane.doe@contoso.com");

        // Exactly 16 lowercase hex, then the domain verbatim. Operators grep on
        // this shape and dead-letter joins slice on the hash length; widening or
        // upper-casing it silently breaks both.
        Assert.Matches("^[0-9a-f]{16}@contoso[.]com$", masked);
        Assert.DoesNotContain("jane", masked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("doe", masked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaskEmail_HashIsTheUnsaltedSha256PrefixThatDeadLetterRecordsUse()
    {
        // The stated contract is that a hash in a log line JOINS against a hash
        // in a dead-letter record (Altrata's DeadLetterPolicy.HashSubject uses
        // the identical construction). Swapping in a salt, an HMAC, a different
        // digest or a different truncation would be invisible in every other
        // test here and would silently break every cross-artefact investigation.
        const string address = "jane.doe@contoso.com";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address)))
            .ToLowerInvariant()[..16];

        Assert.Equal("6f0733d2bb8a63de", expected);          // golden value, pins the algorithm itself
        Assert.Equal($"{expected}@contoso.com", LogRedaction.MaskEmail(address));

        // The same digest is what MaskId produces for the same string, so the
        // local part of a masked email IS the masked id of that address.
        Assert.Equal(expected, LogRedaction.MaskId(address));
    }

    [Fact]
    public void MaskEmail_IsStableAcrossCallsAndTrimsSurroundingWhitespace()
    {
        // Stability is the entire operational value: "this mailbox failed 40
        // times" is only visible if the 40 lines carry the same token.
        var a = LogRedaction.MaskEmail("jane.doe@contoso.com");
        var b = LogRedaction.MaskEmail("jane.doe@contoso.com");
        var padded = LogRedaction.MaskEmail("  \t jane.doe@contoso.com \r\n ");

        Assert.Equal(a, b);
        Assert.Equal(a, padded);
    }

    [Fact]
    public void MaskEmail_DistinguishesDifferentMailboxesInTheSameDomain()
    {
        // Collisions would merge two people into one token and make the log
        // actively misleading. 16 hex is 64 bits; distinctness across a handful
        // of near-identical local parts is the cheap regression guard.
        string[] addresses =
        [
            "jane.doe@contoso.com",
            "jane.doo@contoso.com",
            "jane.doe@contoso.co",
            "ane.doe@contoso.com",
            "jane.doe@fabrikam.com",
        ];

        var masked = addresses.Select(LogRedaction.MaskEmail).ToArray();
        Assert.Equal(masked.Length, masked.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MaskEmail_HashesTheWholeAddressSoTheSameLocalPartDiffersPerDomain()
    {
        // Hashing the WHOLE address (not the local part) is what stops a leaked
        // log from being cross-tenant joinable: jane@a and jane@b are unrelated
        // tokens even though the mailbox name is identical.
        var a = LogRedaction.MaskEmail("jane@contoso.com");
        var b = LogRedaction.MaskEmail("jane@fabrikam.com");

        Assert.NotEqual(a[..16], b[..16]);
    }

    [Fact]
    public void MaskEmail_PreservesTheDomainVerbatimIncludingCaseAndSubdomains()
    {
        // Triage reads the domain: "every unresolved user is @contoso.com" is a
        // tenant/federation diagnosis. It is copied through untouched — no
        // lower-casing, no stripping of subdomains.
        Assert.EndsWith("@MAIL.Contoso.CO.UK", LogRedaction.MaskEmail("a@MAIL.Contoso.CO.UK"), StringComparison.Ordinal);
        Assert.EndsWith("@b", LogRedaction.MaskEmail("a@b"), StringComparison.Ordinal);
        Assert.EndsWith("@[192.0.2.1]", LogRedaction.MaskEmail("a@[192.0.2.1]"), StringComparison.Ordinal);
    }

    [Fact]
    public void MaskEmail_SplitsOnTheLastAtSoOnlyTheRealDomainIsPublished()
    {
        // A quoted local part may legally contain '@'. LastIndexOf keeps the
        // domain correct and, more importantly, keeps the extra '@' inside the
        // hashed half rather than publishing part of the local part in clear.
        var masked = LogRedaction.MaskEmail("\"odd@name\"@contoso.com");

        Assert.Matches("^[0-9a-f]{16}@contoso[.]com$", masked);
        Assert.DoesNotContain("odd", masked, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // No '@' at all.
    [InlineData("not-an-address")]
    // '@' at index 0 — nothing before it, so there is no local part to protect
    // and no reason to trust the tail as a domain.
    [InlineData("@contoso.com")]
    // '@' at the very end — no domain to publish.
    [InlineData("user@")]
    // Degenerate: '@' is the whole value.
    [InlineData("@")]
    public void MaskEmail_HashesNonAddressesWholeRatherThanPublishingAnArbitraryTail(string input)
    {
        var masked = LogRedaction.MaskEmail(input);

        // Bare 16 hex, no '@' and nothing of the input echoed. The alternative —
        // treating whatever follows an '@' as a domain — would put attacker- or
        // user-controlled text straight into a retained log.
        Assert.Matches("^[0-9a-f]{16}$", masked);
        Assert.Equal(LogRedaction.MaskId(input.Trim()), masked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void MaskEmail_RendersAbsentValuesAsTheNonePlaceholderWithoutThrowing(string? input)
    {
        // A null must read distinctly from a redaction, otherwise "we masked an
        // address here" and "there was no address here" become the same log line
        // and a caller cannot tell a privacy control from a missing field.
        Assert.Equal(LogRedaction.None, LogRedaction.MaskEmail(input));
        Assert.Equal("<none>", LogRedaction.None);

        // <none> can never be confused with a hash: hashes are bare hex.
        Assert.DoesNotMatch("^[0-9a-f]{16}", LogRedaction.None);
    }

    [Fact]
    public void MaskEmail_DoesNotThrowOnPathologicalInput()
    {
        // MaskEmail sits on an error path (an Entra lookup that already failed),
        // so it is called with whatever junk the upstream system returned. It
        // throwing there would turn a logged warning into a crash.
        Assert.Matches("^[0-9a-f]{16}$", LogRedaction.MaskEmail(new string('x', 100_000)));
        Assert.Matches("^[0-9a-f]{16}$", LogRedaction.MaskEmail("\0"));
        Assert.Matches("^[0-9a-f]{16}$", LogRedaction.MaskEmail("😀"));      // astral pair
        Assert.Matches("^[0-9a-f]{16}$", LogRedaction.MaskEmail("\ud800"));            // lone surrogate
        Assert.Matches("^[0-9a-f]{16}@пример[.]рф$", LogRedaction.MaskEmail("пользователь@пример.рф"));
    }

    [Fact]
    public void MaskEmail_IsCaseSensitiveSoTheSameMailboxInTwoCasingsGetsTwoTokens()
    {
        // DOCUMENTS CURRENT BEHAVIOUR (reported as a defect, not fixed here).
        // The digest is over the raw trimmed string, so an address that arrives
        // from Graph as "Jane.Doe@contoso.com" and from the source system as
        // "jane.doe@contoso.com" produces two unrelated tokens — correlation,
        // the stated purpose of the hash, silently fails for that user, and the
        // dead-letter join fails with it. Only whitespace is normalised.
        Assert.NotEqual(
            LogRedaction.MaskEmail("Jane.Doe@contoso.com"),
            LogRedaction.MaskEmail("jane.doe@contoso.com"));

        // The domain half, being copied not hashed, keeps its original casing on
        // both sides — so the two tokens are visibly "the same domain, different
        // person" to a reader who cannot know better.
        Assert.EndsWith("@contoso.com", LogRedaction.MaskEmail("Jane.Doe@contoso.com"), StringComparison.Ordinal);
    }

    [Fact]
    public void MaskEmail_CopiesTheDomainThroughUnvalidatedIncludingEmbeddedNewlines()
    {
        // DOCUMENTS CURRENT BEHAVIOUR (reported as a defect, not fixed here).
        // Trim() only removes LEADING and TRAILING whitespace; an interior
        // newline in the "domain" survives and is written to connector.log
        // verbatim, so a crafted address forges whole log lines in a file that
        // is mirrored to the Event Log and shipped to SIEM. MaskEmail is the
        // sanitiser on that path, and it sanitises only the local part.
        const string crafted = "x@contoso.com\n2026-08-14 12:00:00 [ERROR  ] forged: tampered";
        var masked = LogRedaction.MaskEmail(crafted);

        Assert.Contains("\n", masked, StringComparison.Ordinal);
        Assert.Contains("forged: tampered", masked, StringComparison.Ordinal);

        // MaskId is the contrast: it emits nothing but hex, whatever it is fed.
        Assert.Matches("^[0-9a-f]{16}$", LogRedaction.MaskId(crafted));
    }

    [Fact]
    public void MaskEmail_IsReversibleByDictionaryOverAKnownUserList()
    {
        // DOCUMENTS THE DESIGNED LIMIT, not a defect: the digest is unsalted and
        // unkeyed, so anyone holding a candidate list recovers the address in
        // one pass. This test exists so nobody later reads "hashed" in a review
        // and downgrades the log's retention or access controls on the strength
        // of it. Closing the gap needs a keyed HMAC and a per-deployment secret.
        string[] directory = ["alice@contoso.com", "jane.doe@contoso.com", "bob@contoso.com"];
        var masked = LogRedaction.MaskEmail("jane.doe@contoso.com");

        var recovered = directory.Single(
            candidate => string.Equals(LogRedaction.MaskEmail(candidate), masked, StringComparison.Ordinal));

        Assert.Equal("jane.doe@contoso.com", recovered);
    }
}

/// <summary>
/// <see cref="LogRedaction.MaskId"/>: the "nothing in clear" variant used for
/// owner/subject/user ids, where there is no domain worth keeping.
/// </summary>
public class LogRedactionMaskIdTests
{
    [Fact]
    public void MaskId_EmitsOnlyHexSoNoPartOfTheIdSurvives()
    {
        // Unlike MaskEmail this has no clear-text half at all, which is why it
        // is the right tool for anything user-controlled.
        var masked = LogRedaction.MaskId("005Ab000001XyZaBC");

        Assert.Matches("^[0-9a-f]{16}$", masked);
        Assert.DoesNotContain("005Ab", masked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaskId_IsStableTrimmedAndDistinct()
    {
        Assert.Equal(LogRedaction.MaskId("subject-1"), LogRedaction.MaskId("  subject-1  "));
        Assert.NotEqual(LogRedaction.MaskId("subject-1"), LogRedaction.MaskId("subject-2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void MaskId_RendersAbsentValuesAsTheNonePlaceholder(string? input) =>
        Assert.Equal(LogRedaction.None, LogRedaction.MaskId(input));

    [Fact]
    public void MaskId_MatchesMaskEmailsHashHalfSoTheTwoRedactionsJoin()
    {
        // A subject logged once as an id and once as an address must resolve to
        // the same token, or a DSAR/dead-letter investigation cannot follow it
        // across the two call sites.
        const string address = "jane.doe@contoso.com";
        var email = LogRedaction.MaskEmail(address);

        Assert.Equal(LogRedaction.MaskId(address), email[..email.IndexOf('@', StringComparison.Ordinal)]);
    }
}

/// <summary>
/// Base <see cref="StandardLogDialect"/> behaviour. The base class IS the
/// historical Clarizen/Hadoop contract — a host that wires nothing must behave
/// exactly as it did before the dialect type existed — so every default here is
/// an observable promise to three connectors' operators, not an implementation
/// detail.
/// </summary>
public class StandardLogDialectBaseTests
{
    [Fact]
    public void EffectiveLevel_DelegatesToTheChassisDefault()
    {
        using var scope = new DialectTestScope();

        // Delegation, asserted without pinning a value: whatever
        // DefaultEffectiveLevel computes from the live console/file/LOG_LEVEL
        // state, the base dialect must return exactly that. A base override that
        // hard-coded a level would pass a value test and fail this one.
        Assert.Equal(Logging.DefaultEffectiveLevel(), new StandardLogDialect().EffectiveLevel());
        Assert.Equal(Logging.EffectiveLevel(), new StandardLogDialect().EffectiveLevel());
    }

    [Fact]
    public void EffectiveLevel_TreatsLogLevelAsAFloorThatOnlyEverRaises()
    {
        using var scope = new DialectTestScope();
        // ResetForTests closes any run-directory writer, so "the sinks" reduce to
        // the console threshold and the arithmetic below is deterministic. Left
        // open by another test, the file sink would pull the answer to DEBUG.
        Logging.ResetForTests();
        scope.RewireSinks();

        var dialect = new StandardLogDialect();

        Logging.ConsoleLevel = LogLevel.Warning;
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        Assert.Equal(LogLevel.Warning, dialect.EffectiveLevel());

        // LOG_LEVEL=DEBUG cannot LOWER the answer below the console threshold —
        // the floor raises, never drops. Connectors rely on this: --verbose is
        // what turns DEBUG on, not LOG_LEVEL.
        Environment.SetEnvironmentVariable("LOG_LEVEL", "DEBUG");
        Assert.Equal(LogLevel.Warning, dialect.EffectiveLevel());

        // ...and it does raise.
        Environment.SetEnvironmentVariable("LOG_LEVEL", "ERROR");
        Assert.Equal(LogLevel.Error, dialect.EffectiveLevel());

        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        Logging.ConsoleLevel = LogLevel.Debug;                 // the --verbose case
        Assert.Equal(LogLevel.Debug, dialect.EffectiveLevel());
    }

    [Theory]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Info", LogLevel.Info)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("warn", LogLevel.Warning)]          // documented alias
    [InlineData("ERROR", LogLevel.Error)]
    public void SinkFloor_ParsesLogLevelCaseInsensitively(string raw, LogLevel expected)
    {
        using var scope = new DialectTestScope();
        Environment.SetEnvironmentVariable("LOG_LEVEL", raw);

        Assert.Equal(expected, new StandardLogDialect().SinkFloor());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CRITICAL")]     // a real Python level name the chassis does not model
    [InlineData("verbose")]
    [InlineData("off")]          // reads like "log nothing"; means "log everything"
    public void SinkFloor_FailsOpenToDebugForUnsetOrUnrecognisedLogLevel(string? raw)
    {
        using var scope = new DialectTestScope();
        Environment.SetEnvironmentVariable("LOG_LEVEL", raw);

        // Fail-open is the deliberate choice — a typo in LOG_LEVEL must not
        // silently blind the run log. Flipping this to fail-closed would make a
        // misconfigured node stop logging with no other symptom.
        Assert.Equal(LogLevel.Debug, new StandardLogDialect().SinkFloor());
    }

    [Fact]
    public void SinkFloor_ParsesUnderACultureWhoseUpperCasingIsNotInvariant()
    {
        using var scope = new DialectTestScope();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // Turkish maps 'i' to 'İ', so a ToUpper() (rather than
            // ToUpperInvariant()) here would turn "info" into "İNFO", miss every
            // arm of the switch and fail open to DEBUG — a node in a tr-TR
            // locale would quietly log at a different level from its siblings.
            // On a host running in globalization-invariant mode (no ICU) tr-TR
            // collapses to the invariant culture and this is merely redundant
            // rather than wrong, so it is safe unguarded on Linux and Windows.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Environment.SetEnvironmentVariable("LOG_LEVEL", "info");
            Assert.Equal(LogLevel.Info, new StandardLogDialect().SinkFloor());
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void SinkFloorAndEffectiveLevel_AreSeparateGatesByDesign()
    {
        using var scope = new DialectTestScope();
        Logging.ResetForTests();
        scope.RewireSinks();
        Logging.ConsoleLevel = LogLevel.Warning;
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);

        var dialect = new StandardLogDialect();

        // The historical default ADVERTISES a higher level than it ENFORCES: the
        // hot-path gate says "don't bother formatting an INFO record" while the
        // sink still accepts one that is offered. Collapsing the two — either
        // direction — either strips INFO out of connector.log or removes the
        // hot-path skip that IsEnabledFor exists for.
        Assert.Equal(LogLevel.Warning, dialect.EffectiveLevel());
        Assert.Equal(LogLevel.Debug, dialect.SinkFloor());

        Logging.Write(LogLevel.Info, "chassis.dialect", "accepted anyway");
        Assert.Single(scope.Records);
    }

    [Fact]
    public void ComposeMessage_FoldsTheExceptionOntoASecondLineWithABareNewline()
    {
        var dialect = new StandardLogDialect();
        // Never thrown, so ToString() is the type and message with no stack
        // trace — which keeps this assertion identical on Linux and Windows.
        var ex = new InvalidOperationException("boom");

        Assert.Equal("failed", dialect.ComposeMessage("failed", null));
        Assert.Equal(
            "failed\nSystem.InvalidOperationException: boom",
            dialect.ComposeMessage("failed", ex));

        // '\n', not Environment.NewLine. The chassis has always written a bare
        // LF here; a Windows build that started emitting CRLF would change every
        // multi-line record in connector.log and break line-based log shippers
        // that were tuned against the POSIX output.
        Assert.DoesNotContain("\r", dialect.ComposeMessage("failed", ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Mirror_ForwardsEveryLevelToTheHostHookAndToleratesNoHook()
    {
        using var scope = new DialectTestScope();
        var dialect = new StandardLogDialect();

        // No severity filter in the base: the host's Event Log sink decides. A
        // filter added here would silently drop entries a connector's operator
        // runbook expects to find in the Windows Event Log.
        foreach (var level in new[] { LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error })
            dialect.Mirror(new StandardLogEvent(level, "chassis.dialect", $"m-{level}", null, null));

        Assert.Equal(4, scope.Mirrored.Count);
        Assert.Equal((LogLevel.Debug, "chassis.dialect", "m-Debug"), scope.Mirrored[0]);

        // The hook is optional (Seismic hosts never wire it); an unset hook is a
        // no-op, not a NullReferenceException on the logging path.
        Logging.EventLogMirrorHook = null;
        dialect.Mirror(new StandardLogEvent(LogLevel.Error, "chassis.dialect", "m", null, null));
    }

    [Fact]
    public void Render_ProducesTheHistoricalFixedWidthTextLine()
    {
        using var scope = new DialectTestScope();
        var dialect = new StandardLogDialect();

        var line = dialect.Render(new StandardLogEvent(LogLevel.Info, "chassis.dialect", "hello", null, null));

        // "ts [LEVEL  ] logger: msg" — the level is left-aligned in a 7-column
        // field so the columns line up in a terminal and in a SIEM regex. Note
        // there is no correlation segment at all when the id is null.
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[INFO   \] chassis\.dialect: hello$",
            line);
    }

    [Theory]
    [InlineData(LogLevel.Debug, "DEBUG  ")]
    [InlineData(LogLevel.Info, "INFO   ")]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR  ")]
    public void Render_PadsEveryLevelNameToSevenColumns(LogLevel level, string expected)
    {
        using var scope = new DialectTestScope();

        var line = new StandardLogDialect().Render(new StandardLogEvent(level, "l", "m", null, null));

        Assert.Contains($"[{expected}]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PrefixesTheFirstEightCharactersOfTheCorrelationId()
    {
        using var scope = new DialectTestScope();

        var line = new StandardLogDialect().Render(
            new StandardLogEvent(LogLevel.Info, "l", "m", null, "0123456789abcdef0123456789abcdef"));

        // Exactly 8, so a run is greppable without the line turning into a GUID
        // with a message stapled on.
        Assert.Contains("[01234567] l: m", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789a", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ThrowsOnACorrelationIdShorterThanEightCharacters()
    {
        // DOCUMENTS CURRENT BEHAVIOUR (reported as a defect, not fixed here).
        // The text renderer slices [..8] unconditionally. A host whose
        // CorrelationIdProvider returns a short token — or an empty string
        // instead of null — throws from inside Logging.Write, which does NOT
        // guard the render step, so the exception escapes into the caller's
        // code path. The chassis's stated posture elsewhere in Write is
        // "logging must never take the process down".
        using var scope = new DialectTestScope();
        var dialect = new StandardLogDialect();

        // Exactly 8 is the boundary and is fine.
        dialect.Render(new StandardLogEvent(LogLevel.Info, "l", "m", null, "abcdefgh"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => dialect.Render(new StandardLogEvent(LogLevel.Info, "l", "m", null, "abcdefg")));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => dialect.Render(new StandardLogEvent(LogLevel.Info, "l", "m", null, "")));

        // ...and it is not contained by the sink.
        Chassis.CorrelationIdProvider = () => "short";
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Logging.Write(LogLevel.Error, "chassis.dialect", "would have been logged"));
    }

    [Fact]
    public void Render_UnderJsonFormatEmitsTheDocumentedKeysAndOmitsANullCorrelationId()
    {
        using var scope = new DialectTestScope();
        Environment.SetEnvironmentVariable("LOG_FORMAT", "json");
        var dialect = new StandardLogDialect();

        using var withoutId = JsonDocument.Parse(
            dialect.Render(new StandardLogEvent(LogLevel.Warning, "chassis.dialect", "hello", null, null)));
        var root = withoutId.RootElement;

        Assert.Equal("WARNING", root.GetProperty("level").GetString());
        Assert.Equal("chassis.dialect", root.GetProperty("logger").GetString());
        Assert.Equal("hello", root.GetProperty("message").GetString());
        // snake_case, and ABSENT rather than null when there is no scope — the
        // Clarizen/Hadoop contract. (Altrata's dialect deliberately differs on
        // both counts; that is why this member is virtual.)
        Assert.False(root.TryGetProperty("correlation_id", out _));

        // ISO-8601 round-trip in UTC, so lines from nodes in different zones sort.
        var timestamp = DateTimeOffset.Parse(
            root.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);

        using var withId = JsonDocument.Parse(
            dialect.Render(new StandardLogEvent(LogLevel.Info, "l", "m", null, "cid-12345678")));
        // The JSON path does not slice the id, so it carries the WHOLE value —
        // unlike the text path, which shows only the first 8.
        Assert.Equal("cid-12345678", withId.RootElement.GetProperty("correlation_id").GetString());
    }

    [Fact]
    public void Render_UnderJsonFormatHasNoExceptionFieldBecauseTheMessageAlreadyCarriesIt()
    {
        using var scope = new DialectTestScope();
        Environment.SetEnvironmentVariable("LOG_FORMAT", "json");

        // The base pairing is ComposeMessage folding + a renderer with no
        // exception field. Overriding one without the other loses the exception
        // entirely, which is exactly the trap this pairing test guards.
        Logging.Write(LogLevel.Error, "chassis.dialect", "failed", new InvalidOperationException("boom"));

        using var doc = JsonDocument.Parse(Assert.Single(scope.Lines));
        Assert.False(doc.RootElement.TryGetProperty("exception", out _));
        Assert.Contains("System.InvalidOperationException: boom",
            doc.RootElement.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesToConsole_TracksTheLiveConsoleLevel()
    {
        using var scope = new DialectTestScope();
        var dialect = new StandardLogDialect();
        var info = new StandardLogEvent(LogLevel.Info, "l", "m", null, null);
        var warning = new StandardLogEvent(LogLevel.Warning, "l", "m", null, null);

        Logging.ConsoleLevel = LogLevel.Warning;
        Assert.False(dialect.WritesToConsole(info));
        Assert.True(dialect.WritesToConsole(warning));

        // Read per record, not captured at construction: --verbose flips
        // ConsoleLevel after the dialect is already wired.
        Logging.ConsoleLevel = LogLevel.Debug;
        Assert.True(dialect.WritesToConsole(info));
    }

    [Fact]
    public void WritesToStandardError_SendsWarningAndAboveToStderr()
    {
        var dialect = new StandardLogDialect();

        // WARNING on stderr is the base (Clarizen/Hadoop) contract. It is not
        // universal — Altrata puts only ERROR there — so a change here would
        // silently reroute two connectors' warnings in every shell pipeline and
        // supervisor that separates the streams.
        Assert.False(dialect.WritesToStandardError(new StandardLogEvent(LogLevel.Debug, "l", "m", null, null)));
        Assert.False(dialect.WritesToStandardError(new StandardLogEvent(LogLevel.Info, "l", "m", null, null)));
        Assert.True(dialect.WritesToStandardError(new StandardLogEvent(LogLevel.Warning, "l", "m", null, null)));
        Assert.True(dialect.WritesToStandardError(new StandardLogEvent(LogLevel.Error, "l", "m", null, null)));
    }

    [Fact]
    public void StandardLogEvent_IsAValueRecordSoDialectsCanCompareAndCopyIt()
    {
        var ex = new InvalidOperationException("boom");
        var a = new StandardLogEvent(LogLevel.Info, "l", "m", ex, "cid");
        var b = new StandardLogEvent(LogLevel.Info, "l", "m", ex, "cid");

        // Value equality and `with` are part of the shape a dialect writes
        // against (a dialect commonly re-renders a copy with a tweaked message).
        Assert.Equal(a, b);
        Assert.Equal("other", (a with { Message = "other" }).Message);
        Assert.Equal(ex, a.Exception);
    }
}

/// <summary>
/// The seam itself: a subclass's overrides must actually change what
/// <see cref="Logging.Write"/> does. A dialect whose overrides are ignored is
/// the failure worth catching — it compiles, it unit-tests green in isolation,
/// and it silently reverts a connector's whole log contract.
/// </summary>
public class StandardLogDialectSeamTests
{
    [Fact]
    public void SinkFloor_OverrideDropsRecordsBeforeEverySeamMirrorRenderAndConsole()
    {
        using var scope = new DialectTestScope();
        // LOG_LEVEL says DEBUG, so the BASE floor would admit everything. If the
        // sink consulted the environment directly instead of the dialect, the
        // WARNING below would sail through and this test would fail.
        Environment.SetEnvironmentVariable("LOG_LEVEL", "DEBUG");
        Logging.ConsoleLevel = LogLevel.Debug;
        Logging.Dialect = new FloorDialect(LogLevel.Error);

        Logging.Write(LogLevel.Warning, "chassis.dialect", "dropped");

        Assert.Empty(scope.Records);
        Assert.Empty(scope.Lines);
        Assert.Empty(scope.Mirrored);
        Assert.Equal(string.Empty, scope.Stdout.ToString());
        Assert.Equal(string.Empty, scope.Stderr.ToString());

        Logging.Write(LogLevel.Error, "chassis.dialect", "kept");
        Assert.Single(scope.Records);
        Assert.Single(scope.Lines);
        Assert.Single(scope.Mirrored);
    }

    [Fact]
    public void ComposeMessage_OverrideChangesWhatTheTestSinkMirrorAndRendererAllSee()
    {
        using var scope = new DialectTestScope();
        var ex = new InvalidOperationException("boom");

        // Base: the exception is folded into "the message", so it reaches the
        // Event Log mirror and the rendered line as text.
        Logging.Write(LogLevel.Error, "chassis.dialect", "failed", ex);
        Assert.Contains("System.InvalidOperationException", scope.Records[0].Message, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", scope.Mirrored[0].Message, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", scope.Lines[0], StringComparison.Ordinal);

        scope.Clear();
        var dialect = new NoFoldDialect();
        Logging.Dialect = dialect;

        // Override: the message is the message. The exception is still carried
        // on the event for a renderer that wants its own field, but it no longer
        // leaks a stack trace into the Windows Event Log entry.
        Logging.Write(LogLevel.Error, "chassis.dialect", "failed", ex);
        Assert.Equal("failed", scope.Records[0].Message);
        Assert.Equal("failed", scope.Mirrored[0].Message);
        Assert.DoesNotContain("System.InvalidOperationException", scope.Lines[0], StringComparison.Ordinal);
        Assert.Equal("boom", Assert.Single(dialect.SeenExceptions).Message);
    }

    [Fact]
    public void Mirror_OverrideReplacesTheHostHookRatherThanRunningAlongsideIt()
    {
        using var scope = new DialectTestScope();
        var dialect = new SelectiveMirrorDialect();
        Logging.Dialect = dialect;

        Logging.Write(LogLevel.Info, "chassis.dialect", "quiet");
        Logging.Write(LogLevel.Warning, "chassis.dialect", "loud");

        // EventLogMirrorHook is only ever reached THROUGH the dialect. A sink
        // that also called the hook itself would double-write every Event Log
        // entry for a host with a filtering dialect — and would defeat the
        // filtering entirely.
        Assert.Empty(scope.Mirrored);
        Assert.Equal(["WARNING/loud"], dialect.Seen);
    }

    [Fact]
    public void Render_OverrideIsTheExactStringHandedToTheRenderedSinkAndTheConsole()
    {
        using var scope = new DialectTestScope();
        Logging.ConsoleLevel = LogLevel.Debug;
        Logging.Dialect = new FixedLineDialect();

        Logging.Write(LogLevel.Warning, "chassis.dialect", "hello");

        // Byte-for-byte: no chassis timestamp, no level bracket, nothing bolted
        // on around the dialect's line.
        Assert.Equal("FIXED|WARNING|chassis.dialect|hello", Assert.Single(scope.Lines));
        Assert.Equal("FIXED|WARNING|chassis.dialect|hello" + Environment.NewLine, scope.Stderr.ToString());
    }

    [Fact]
    public async Task Render_OverrideReachesTheRunDirectoryLogFile()
    {
        // The rendered line has to land in connector.log, not just in the test
        // seam — the file is the artefact an operator actually reads.
        using var scope = new DialectTestScope();
        // Logging.Initialize flips the process-wide mode to Standard as a side
        // effect; entering the CURRENT mode here is how it gets put back.
        using var mode = new LoggingModeScope(Logging.Mode);
        var temp = Directory.CreateTempSubdirectory("chassis-dialect-");
        try
        {
            Logging.ResetForTests();                       // guarantee no writer is already open
            scope.RewireSinks();
            Logging.LogsRoot = Path.Combine(temp.FullName, "logs");
            Logging.HardenDirectoryHook = d => Directory.CreateDirectory(d);
            Logging.Dialect = new FixedLineDialect();

            Logging.Initialize("dialecttest", verbose: false);
            Assert.NotNull(Logging.RunDirectory);
            var logPath = Path.Combine(Logging.RunDirectory!, "connector.log");

            Logging.Write(LogLevel.Error, "chassis.dialect", "to-file");

            // Close the writer BEFORE reading: Windows opens the log with a share
            // mode that refuses a concurrent reader, and refuses the directory
            // delete below outright while the handle is live.
            Logging.ResetForTests();
            scope.RewireSinks();

            Assert.Equal("FIXED|ERROR|chassis.dialect|to-file", (await File.ReadAllLinesAsync(logPath)).Single());
        }
        finally
        {
            Logging.ResetForTests();
            try
            {
                temp.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a temp directory left behind must not fail the run
                // (Windows can still be holding the handle for a moment).
            }
        }
    }

    [Fact]
    public void ConsoleRouting_OverridesRedirectRecordsBetweenStdoutStderrAndNowhere()
    {
        using var scope = new DialectTestScope();
        // ConsoleLevel=Error means the BASE would suppress INFO entirely and put
        // WARNING on stderr. Both are overridden below, so any assertion that
        // still matches the base behaviour means the sink ignored the dialect.
        Logging.ConsoleLevel = LogLevel.Error;
        Logging.Dialect = new ConsoleRoutingDialect();

        Logging.Write(LogLevel.Info, "chassis.dialect", "i");
        Logging.Write(LogLevel.Warning, "chassis.dialect", "w");
        Logging.Write(LogLevel.Error, "chassis.dialect", "e");

        var stdout = scope.Stdout.ToString();
        var stderr = scope.Stderr.ToString();

        Assert.Contains("R|Info|i", stdout, StringComparison.Ordinal);      // base would have dropped it
        Assert.Contains("R|Warning|w", stdout, StringComparison.Ordinal);   // base would have used stderr
        Assert.DoesNotContain("R|Warning|w", stderr, StringComparison.Ordinal);
        Assert.Contains("R|Error|e", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("R|Error|e", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsoleRouting_SuppressionDoesNotSuppressTheFileOrTestSeams()
    {
        using var scope = new DialectTestScope();
        Logging.Dialect = new NeverConsoleDialect();

        Logging.Write(LogLevel.Error, "chassis.dialect", "silent");

        // WritesToConsole gates ONLY the console. A dialect that quietens a noisy
        // service must not also empty connector.log — that is the difference
        // between "quiet" and "undiagnosable".
        Assert.Equal(string.Empty, scope.Stdout.ToString());
        Assert.Equal(string.Empty, scope.Stderr.ToString());
        Assert.Single(scope.Lines);
        Assert.Single(scope.Records);
        Assert.Single(scope.Mirrored);
    }

    [Fact]
    public void EffectiveLevel_OverrideDrivesLoggingEffectiveLevelAndTheStandardHotPathGate()
    {
        using var scope = new DialectTestScope();
        using var mode = new LoggingModeScope(LoggingMode.Standard);
        Logging.ConsoleLevel = LogLevel.Debug;                 // base would advertise DEBUG
        Logging.Dialect = new FixedEffectiveLevelDialect(LogLevel.Error);

        Assert.Equal(LogLevel.Error, Logging.EffectiveLevel());

        // The whole point of the gate: callers skip building expensive messages
        // for records that will be dropped. If IsEnabledFor stopped consulting
        // the dialect, an Altrata-style host would either format DEBUG messages
        // it never emits, or skip formatting records it does.
        var log = Logging.GetLogger("chassis.dialect.gate");
        Assert.False(log.IsEnabledFor(LogLevel.Warning));
        Assert.True(log.IsEnabledFor(LogLevel.Error));
    }

    [Fact]
    public void EffectiveLevel_IsAdvertisementOnlyAndDoesNotGateTheSink()
    {
        using var scope = new DialectTestScope();
        // A dialect that raises only EffectiveLevel must not lose records: the
        // sink gate is SinkFloor. Wiring the advertisement to the gate by
        // accident would silently truncate connector.log for such a host.
        Logging.Dialect = new FixedEffectiveLevelDialect(LogLevel.Error);

        Logging.Write(LogLevel.Debug, "chassis.dialect", "still written");

        Assert.Single(scope.Records);
        Assert.Single(scope.Lines);
    }

    [Fact]
    public void Write_SnapshotsTheDialectSoAMidWriteSwapCannotSplitPolicy()
    {
        using var scope = new DialectTestScope();
        var original = new SelfReplacingDialect();
        Logging.Dialect = original;

        Logging.Write(LogLevel.Error, "chassis.dialect", "one record");

        // Logging.Write reads Logging.Dialect exactly once. Without that
        // snapshot a host swapping dialects at runtime (or a test doing so
        // concurrently) could gate a record with one policy and render it with
        // another — a line that passed an Altrata floor but came out in
        // Clarizen format, which no log parser is prepared for.
        Assert.True(original.Rendered);
        Assert.Equal("FROM-ORIGINAL", Assert.Single(scope.Lines));
        Assert.NotSame(original, Logging.Dialect);
    }

    [Fact]
    public void Dialect_IsHotSwappableAndTheNextRecordUsesTheNewPolicy()
    {
        using var scope = new DialectTestScope();

        Logging.Write(LogLevel.Error, "chassis.dialect", "before");
        Logging.Dialect = new FixedLineDialect();
        Logging.Write(LogLevel.Error, "chassis.dialect", "after");

        // Hosts wire the dialect in Program startup, but the chassis caches
        // nothing per-logger; a logger obtained before the wiring must still get
        // the host's format.
        Assert.EndsWith("chassis.dialect: before", scope.Lines[0], StringComparison.Ordinal);
        Assert.Equal("FIXED|ERROR|chassis.dialect|after", scope.Lines[1]);
    }

    [Fact]
    public void Dialect_IsIgnoredInSeismicModeWhereTheHandlerPipelineOwnsGating()
    {
        using var scope = new DialectTestScope();
        using var mode = new LoggingModeScope(LoggingMode.Seismic);
        Logging.Dialect = new FixedEffectiveLevelDialect(LogLevel.Debug);

        var log = Logging.GetLoggerObject("chassis.dialect.seismic");
        var level = log.Level;
        try
        {
            log.Level = LogLevels.Error;

            // The dialect says DEBUG, but Seismic mode answers from the handler
            // hierarchy. The seam is Standard-only by design — the Seismic path
            // must stay byte-identical to the pre-chassis behaviour.
            Assert.False(log.IsEnabledFor(LogLevel.Warning));
            Assert.True(log.IsEnabledFor(LogLevel.Error));
        }
        finally
        {
            log.Level = level;
        }
    }

    // ── dialects under test ──────────────────────────────────────────────────

    private sealed class FloorDialect(LogLevel floor) : StandardLogDialect
    {
        public override LogLevel SinkFloor() => floor;
    }

    /// <summary>Altrata-style: the exception belongs in its own field, not in the message.</summary>
    private sealed class NoFoldDialect : StandardLogDialect
    {
        public List<Exception> SeenExceptions { get; } = [];

        public override string ComposeMessage(string message, Exception? exception) => message;

        public override string Render(StandardLogEvent e)
        {
            if (e.Exception is not null)
                SeenExceptions.Add(e.Exception);
            return $"NOFOLD|{e.Message}";
        }
    }

    private sealed class SelectiveMirrorDialect : StandardLogDialect
    {
        public List<string> Seen { get; } = [];

        public override void Mirror(StandardLogEvent e)
        {
            if (e.Level >= LogLevel.Warning)
                Seen.Add($"{e.Level.ToString().ToUpperInvariant()}/{e.Message}");
        }
    }

    private sealed class FixedLineDialect : StandardLogDialect
    {
        public override string Render(StandardLogEvent e) =>
            $"FIXED|{e.Level.ToString().ToUpperInvariant()}|{e.Logger}|{e.Message}";
    }

    private sealed class ConsoleRoutingDialect : StandardLogDialect
    {
        public override string Render(StandardLogEvent e) => $"R|{e.Level}|{e.Message}";

        public override bool WritesToConsole(StandardLogEvent e) => true;

        public override bool WritesToStandardError(StandardLogEvent e) => e.Level == LogLevel.Error;
    }

    private sealed class NeverConsoleDialect : StandardLogDialect
    {
        public override bool WritesToConsole(StandardLogEvent e) => false;
    }

    private sealed class FixedEffectiveLevelDialect(LogLevel level) : StandardLogDialect
    {
        public override LogLevel EffectiveLevel() => level;
    }

    /// <summary>Swaps itself out mid-record, to prove Write took a snapshot.</summary>
    private sealed class SelfReplacingDialect : StandardLogDialect
    {
        public bool Rendered { get; private set; }

        public override string ComposeMessage(string message, Exception? exception)
        {
            Logging.Dialect = new StandardLogDialect();
            return message;
        }

        public override string Render(StandardLogEvent e)
        {
            Rendered = true;
            return "FROM-ORIGINAL";
        }
    }
}

/// <summary>
/// Saves and restores every process-global these tests touch. The chassis is
/// built out of statics and the whole assembly shares them, so a leaked
/// LOG_LEVEL or a dangling Console.SetOut breaks tests written by someone else
/// in a file this one has never seen.
/// </summary>
internal sealed class DialectTestScope : IDisposable
{
    // Field initialisers run before the constructor body, so every original is
    // captured before anything below overwrites it.
    private readonly string? _logLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
    private readonly string? _logFormat = Environment.GetEnvironmentVariable("LOG_FORMAT");
    private readonly StandardLogDialect _dialect = Logging.Dialect;
    private readonly LogLevel _consoleLevel = Logging.ConsoleLevel;
    private readonly string _logsRoot = Logging.LogsRoot;
    private readonly Action<string>? _harden = Logging.HardenDirectoryHook;
    private readonly Action<LogLevel, string, string>? _eventLog = Logging.EventLogMirrorHook;
    private readonly Action<LogLevel, string, string>? _testSink = Logging.TestSink;
    private readonly Action<string>? _renderedSink = Logging.RenderedSink;
    private readonly Func<string?>? _correlation = Chassis.CorrelationIdProvider;
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;

    public DialectTestScope()
    {
        Console.SetOut(Stdout);
        Console.SetError(Stderr);
        Chassis.CorrelationIdProvider = null;
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("LOG_FORMAT", null);
        Logging.Dialect = new StandardLogDialect();
        RewireSinks();
    }

    public StringWriter Stdout { get; } = new();

    public StringWriter Stderr { get; } = new();

    public List<(LogLevel Level, string Logger, string Message)> Records { get; } = [];

    public List<(LogLevel Level, string Logger, string Message)> Mirrored { get; } = [];

    public List<string> Lines { get; } = [];

    /// <summary>
    /// Re-attach the capture seams. Needed after
    /// <see cref="Logging.ResetForTests"/>, which nulls TestSink/RenderedSink
    /// and resets ConsoleLevel as part of forgetting Standard run state.
    /// </summary>
    public void RewireSinks()
    {
        Logging.TestSink = (level, logger, message) => Records.Add((level, logger, message));
        Logging.RenderedSink = Lines.Add;
        Logging.EventLogMirrorHook = (level, logger, message) => Mirrored.Add((level, logger, message));
    }

    public void Clear()
    {
        Records.Clear();
        Mirrored.Clear();
        Lines.Clear();
        Stdout.GetStringBuilder().Clear();
        Stderr.GetStringBuilder().Clear();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOG_LEVEL", _logLevel);
        Environment.SetEnvironmentVariable("LOG_FORMAT", _logFormat);
        Logging.Dialect = _dialect;
        Logging.ConsoleLevel = _consoleLevel;
        Logging.LogsRoot = _logsRoot;
        Logging.HardenDirectoryHook = _harden;
        Logging.EventLogMirrorHook = _eventLog;
        Logging.TestSink = _testSink;
        Logging.RenderedSink = _renderedSink;
        Chassis.CorrelationIdProvider = _correlation;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }
}

/// <summary>
/// Enters a <see cref="LoggingMode"/> and restores the previous one exactly.
///
/// <see cref="Logging.Mode"/> has a private setter and the only public route
/// back to Seismic is <see cref="Logging.Configure"/>, which closes and discards
/// the root logger's handlers — a far bigger global to trample than the mode
/// flag itself. The setter is resolved BEFORE the mode is changed, so a rename
/// fails this test rather than leaving the whole assembly in the wrong mode.
/// </summary>
internal sealed class LoggingModeScope : IDisposable
{
    private readonly MethodInfo _setter;
    private readonly LoggingMode _previous;

    public LoggingModeScope(LoggingMode target)
    {
        _setter = typeof(Logging)
                      .GetProperty(nameof(Logging.Mode), BindingFlags.Public | BindingFlags.Static)
                      ?.GetSetMethod(nonPublic: true)
                  ?? throw new InvalidOperationException(
                      "Logging.Mode has no settable property; this scope can no longer restore it.");
        _previous = Logging.Mode;
        Set(target);
    }

    public void Dispose() => Set(_previous);

    private void Set(LoggingMode mode) => _setter.Invoke(null, [mode]);
}
