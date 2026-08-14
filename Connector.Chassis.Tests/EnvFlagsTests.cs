// EnvFlagsTests.cs
// ----------------
// EnvFlags is the one place the whole fleet agrees on what a boolean
// environment variable MEANS, so every connector's feature gates -- circuit
// breaker, deletion sync, classification, identity sync -- inherit their
// on/off semantics from these four methods. The interesting content is not the
// happy path but the vocabulary boundary: which spellings are recognised, which
// are silently ignored, and what each entry point does with a value it does not
// recognise. Those three answers differ per method ON PURPOSE, and the
// differences are load-bearing:
//
//   * IsTrue            unrecognised -> false   (default-OFF gates)
//   * IsTrueOrDefault   unrecognised -> false   EVEN WHEN fallback is true
//   * IsFalse           unrecognised -> false, so !IsFalse -> true (default-ON gates)
//
// The last two are why IsFalse exists at all. !IsFalse("CIRCUIT_BREAKER") and
// IsTrueOrDefault("CIRCUIT_BREAKER", true) agree on unset and on every
// recognised value, and disagree on exactly the input an operator produces by
// accident -- a typo. Folding one into the other would mean a mistyped env var
// silently disables a protective default. UnrecognisedValue_* below is the test
// that stops that "simplification" from landing.
//
// Every test restores what it touched: the probe vars via Scope, and the
// ambient culture in a finally. IDENTITY_SYNC_ON_INCREMENTAL is a REAL variable
// the fleet reads, so it is saved and restored like the rest.

using System.Globalization;

namespace Connector.Chassis.Tests;

public class EnvFlagsTests
{
    // Probe names are namespaced to this file so nothing else in the assembly
    // (or the ambient shell) can collide with them.
    private const string Flag = "CHASSIS_ENVFLAGS_PROBE";
    private const string Num = "CHASSIS_ENVFLAGS_INT_PROBE";
    private const string IdentitySyncVar = "IDENTITY_SYNC_ON_INCREMENTAL";

    /// <summary>Sets env vars and restores their prior values on dispose.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public Scope(params (string Name, string? Value)[] vars)
        {
            foreach (var (name, value) in vars)
                Set(name, value);
        }

        public void Set(string name, string? value)
        {
            if (!_previous.ContainsKey(name))
                _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    // The whole vocabulary the four methods are asked to classify, used by the
    // cross-method invariants at the bottom. Keep unrecognised spellings in here
    // -- they are the ones that expose disagreement between the methods.
    private static readonly string?[] Vocabulary =
    [
        null, " ", "\t",
        "true", "TRUE", "True", "tRuE", " true ", "1", " 1 ", "yes", "YES", " Yes ",
        "false", "FALSE", "False", " false ", "0", " 0 ", "no", "NO", " No ",
        "flase", "maybe", "off", "on", "y", "n", "t", "f", "2", "-1", "00", "-0",
        "0.0", "true!", "enabled", "disabled",
    ];

    // ---------------------------------------------------------------- GetInt

    [Fact]
    public void GetInt_Unset_ReturnsTheDefaultVerbatim()
    {
        using var env = new Scope((Num, null));

        Assert.Equal(0, EnvFlags.GetInt(Num, 0));
        Assert.Equal(int.MaxValue, EnvFlags.GetInt(Num, int.MaxValue));

        // GetInt does NOT sanity-check the default. Callers that need a floor
        // impose one themselves (SqlGateway wraps its SQL_MAX_RETRIES read in
        // Math.Max); if GetInt ever starts clamping, those wrappers become
        // silently redundant and the real range moves.
        Assert.Equal(-1, EnvFlags.GetInt(Num, -1));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData("007", 7)]
    [InlineData("-7", -7)]
    [InlineData("+7", 7)]            // NumberStyles.Integer allows a leading sign
    [InlineData(" 42 ", 42)]         // ...and leading/trailing whitespace
    [InlineData("\t42\n", 42)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void GetInt_ParsesSignedIntegersWithSurroundingWhitespace(string raw, int expected)
    {
        using var env = new Scope((Num, raw));
        Assert.Equal(expected, EnvFlags.GetInt(Num, -999));
    }

    [Theory]
    [InlineData("")]            // reads as absent on both OSes, see SettingAFlagToEmpty_*
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("true")]        // a bool in an int slot is operator error, not 1
    [InlineData("4 2")]
    [InlineData("3.0")]         // no AllowDecimalPoint, even for a whole number
    [InlineData("1,000")]       // no AllowThousands
    [InlineData("1.000")]       // nor the de-DE spelling of the same
    [InlineData("0x1F")]
    [InlineData("(5)")]         // no AllowParentheses
    [InlineData("12e3")]
    [InlineData("2147483648")]  // int.MaxValue + 1
    [InlineData("-2147483649")] // int.MinValue - 1
    [InlineData("99999999999999999999")]
    public void GetInt_UnparseableValueFallsBack_NeverThrowsAndNeverWraps(string raw)
    {
        // The overflow rows matter most: TryParse fails rather than saturating,
        // so an operator who fat-fingers an extra digit onto GRAPH_MAX_RETRIES
        // gets the documented default, not int.MinValue and not a crash on
        // startup deep inside a config read.
        using var env = new Scope((Num, raw));
        Assert.Equal(9_999, EnvFlags.GetInt(Num, 9_999));
    }

    [Fact]
    public void GetInt_IsCultureInvariant()
    {
        // Pinned to InvariantCulture, so a host running under a European locale
        // reads the same numbers as CI. If someone swaps in CurrentCulture the
        // group-separator rows below start moving with the machine's locale.
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = GermanOrInvariant();
            using var env = new Scope((Num, "1.234"));

            Assert.Equal(-1, EnvFlags.GetInt(Num, -1));   // NOT 1234, even in de-DE
            env.Set(Num, "1,234");
            Assert.Equal(-1, EnvFlags.GetInt(Num, -1));
            env.Set(Num, "-42");
            Assert.Equal(-42, EnvFlags.GetInt(Num, -1));  // plain integers still parse
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>de-DE, or the invariant culture on a globalization-invariant build.</summary>
    private static CultureInfo GermanOrInvariant()
    {
        try
        {
            return CultureInfo.GetCultureInfo("de-DE");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    // ---------------------------------------------------------------- IsTrue

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("tRuE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("Yes", true)]
    [InlineData(null, false)]      // unset: every gate built on IsTrue defaults OFF
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("on", false)]      // plausible English, NOT in the vocabulary
    [InlineData("off", false)]
    [InlineData("enabled", false)]
    [InlineData("y", false)]
    [InlineData("t", false)]
    [InlineData("2", false)]       // only "1" is truthy, not "any nonzero"
    [InlineData("-1", false)]
    [InlineData("01", false)]
    [InlineData("true!", false)]
    [InlineData("maybe", false)]
    public void IsTrue_RecognisesOnly_True1Yes_CaseInsensitively(string? raw, bool expected)
    {
        using var env = new Scope((Flag, raw));
        Assert.Equal(expected, EnvFlags.IsTrue(Flag));
    }

    [Theory]
    [InlineData(" true")]
    [InlineData("true ")]
    [InlineData(" 1 ")]
    [InlineData("\tyes")]
    [InlineData("yes\n")]
    public void IsTrue_TrimsLikeEveryOtherEntryPoint(string raw)
    {
        // This test used to assert the OPPOSITE, pinning IsTrue's missing Trim()
        // as observed behaviour while reporting it as a defect. It was a defect:
        // measured end to end, CLASSIFICATION_ENFORCE_ACL="true " read as OFF
        // *and* suppressed the AppConfig.Validate() guard that would have failed
        // the load, so Altrata indexed Restricted items without locking them to
        // the enforcement group while validate-config reported success.
        //
        // Padding is deployment plumbing, not intent: a trailing space in a .env
        // line, a folded YAML scalar, `ENV FLAG=true ` in a Dockerfile. Every
        // entry point now trims, so all three agree on every input.
        using var env = new Scope((Flag, raw));

        Assert.True(EnvFlags.IsTrue(Flag));
        Assert.True(EnvFlags.IsTrueOrDefault(Flag, fallback: false));
        Assert.False(EnvFlags.IsFalse(Flag));
    }

    // ------------------------------------------------------- IsTrueOrDefault

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \t  ")]
    public void IsTrueOrDefault_UnsetOrBlank_UsesTheFallback(string? raw)
    {
        using var env = new Scope((Flag, raw));
        Assert.True(EnvFlags.IsTrueOrDefault(Flag, fallback: true));
        Assert.False(EnvFlags.IsTrueOrDefault(Flag, fallback: false));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData(" Yes ")]
    [InlineData("\tTRUE\n")]
    public void IsTrueOrDefault_RecognisedOn_WinsOverEitherFallback(string raw)
    {
        using var env = new Scope((Flag, raw));
        Assert.True(EnvFlags.IsTrueOrDefault(Flag, fallback: false));
        Assert.True(EnvFlags.IsTrueOrDefault(Flag, fallback: true));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData(" No ")]
    public void IsTrueOrDefault_RecognisedOff_WinsOverEitherFallback(string raw)
    {
        // The operator's explicit "off" must beat a default-ON flag; this is the
        // only supported way to disable IDENTITY_SYNC_ON_INCREMENTAL and friends.
        using var env = new Scope((Flag, raw));
        Assert.False(EnvFlags.IsTrueOrDefault(Flag, fallback: true));
        Assert.False(EnvFlags.IsTrueOrDefault(Flag, fallback: false));
    }

    // --------------------------------------------------------------- IsFalse

    [Theory]
    [InlineData("false", true)]
    [InlineData("FALSE", true)]
    [InlineData("False", true)]
    [InlineData(" false ", true)]
    [InlineData("0", true)]
    [InlineData(" 0 ", true)]
    [InlineData("no", true)]
    [InlineData("NO", true)]
    [InlineData("\tNo\n", true)]
    [InlineData(null, false)]      // unset is NOT false: !IsFalse leaves the gate ON
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("true", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("off", false)]     // plausible "off" spellings that are not recognised...
    [InlineData("disabled", false)]
    [InlineData("n", false)]
    [InlineData("f", false)]
    [InlineData("00", false)]      // ...and near-misses of the ones that are
    [InlineData("-0", false)]
    [InlineData("0.0", false)]
    [InlineData("flase", false)]
    public void IsFalse_RecognisesOnly_False0No_TrimmedAndCaseInsensitive(string? raw, bool expected)
    {
        using var env = new Scope((Flag, raw));
        Assert.Equal(expected, EnvFlags.IsFalse(Flag));
    }

    // --------------------------------------- the asymmetry that was the defect

    [Theory]
    [InlineData("flase")]      // the actual typo this design exists to survive
    [InlineData("fasle")]
    [InlineData("maybe")]
    [InlineData("off")]
    [InlineData("disabled")]
    [InlineData("n")]
    [InlineData("2")]
    [InlineData("00")]
    [InlineData("-0")]
    [InlineData("0.0")]
    public void UnrecognisedValue_LeavesEveryDefaultOnIdiomON(string raw)
    {
        // This test used to assert that the two idioms DISAGREE here, and called
        // the disagreement the point. It was the defect. IsTrueOrDefault(x, true)
        // returned false for a typo -- breaking its own name, since the caller
        // asked for the value OR the default and got neither -- which is why
        // IsFalse had to exist and why IdentitySyncOnIncremental was wrong.
        //
        // An unrecognised value now means "no opinion" everywhere: it falls back
        // to the caller's declared default, and warns once naming the variable.
        // A value nobody meant can no longer choose a setting in either
        // direction, which is the only reading that is safe for both a
        // default-ON and a default-OFF gate.
        using var env = new Scope((Flag, raw));

        Assert.True(!EnvFlags.IsFalse(Flag));                            // protective default stays ON
        Assert.True(EnvFlags.IsTrueOrDefault(Flag, fallback: true));     // ... and so does this, now
        Assert.False(EnvFlags.IsTrueOrDefault(Flag, fallback: false));   // a default-OFF gate stays OFF
        Assert.False(EnvFlags.IsTrue(Flag));                             // still not an "on"
    }

    [Theory]
    [InlineData("flase")]
    [InlineData("off")]
    [InlineData("2")]
    public void UnrecognisedValue_IsReportedAsAbsent_NotAsFalse(string raw)
    {
        // Parse is the single vocabulary. null means "the operator said nothing
        // I can act on", which is what makes every caller fall back rather than
        // invent an answer.
        using var env = new Scope((Flag, raw));
        Assert.Null(EnvFlags.Parse(Flag));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(" ", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("YES", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData(" No ", false)]
    public void UnsetOrRecognisedValue_TheTwoDefaultOnIdiomsAgree(string? raw, bool expected)
    {
        // The other half of the story: the two idioms are interchangeable for
        // every input an operator gets RIGHT. That is what makes the swap look
        // safe, and why the divergence above has to be pinned explicitly.
        using var env = new Scope((Flag, raw));

        Assert.Equal(expected, !EnvFlags.IsFalse(Flag));
        Assert.Equal(expected, EnvFlags.IsTrueOrDefault(Flag, fallback: true));
    }

    // -------------------------------------------------- cross-method invariants

    [Fact]
    public void IsTrueAndIsFalse_AreNeverBothTrue()
    {
        // The two vocabularies must stay disjoint. If a spelling is ever added to
        // one set and accidentally left in the other, a gate becomes ambiguous:
        // IsTrue says run it, !IsFalse says run it, and IsTrueOrDefault picks a
        // third answer.
        using var env = new Scope((Flag, null));
        foreach (var raw in Vocabulary)
        {
            env.Set(Flag, raw);
            Assert.False(
                EnvFlags.IsTrue(Flag) && EnvFlags.IsFalse(Flag),
                $"'{raw ?? "<unset>"}' is classified as both true and false");
        }
    }

    [Fact]
    public void ARecognisedValueNeverConsultsTheFallback()
    {
        // The fallback is for "the operator gave me nothing I can act on" --
        // which now includes a value outside the vocabulary, because inventing
        // an answer from a typo is exactly how a gate flips silently. A
        // RECOGNISED value must still answer the same regardless of fallback:
        // when the operator did say something, the default is irrelevant.
        using var env = new Scope((Flag, null));
        var recognised = 0;
        foreach (var raw in Vocabulary)
        {
            env.Set(Flag, raw);
            if (EnvFlags.Parse(Flag) is null)
                continue;   // unset, blank or unrecognised — the fallback SHOULD decide

            recognised++;
            Assert.Equal(
                EnvFlags.IsTrueOrDefault(Flag, fallback: true),
                EnvFlags.IsTrueOrDefault(Flag, fallback: false));
        }

        // Guards the guard: if Parse ever stopped recognising anything, the loop
        // above would skip every case and pass vacuously.
        Assert.True(recognised >= 6, $"only {recognised} recognised values in the vocabulary");
    }

    [Fact]
    public void AnUnrecognisedValueDoesConsultTheFallback()
    {
        // The other half, stated explicitly so the pair reads as one rule:
        // recognised -> the value; anything else -> the caller's default.
        using var env = new Scope((Flag, "flase"));

        Assert.True(EnvFlags.IsTrueOrDefault(Flag, fallback: true));
        Assert.False(EnvFlags.IsTrueOrDefault(Flag, fallback: false));
    }

    [Fact]
    public void EveryEntryPointReReadsTheEnvironmentOnEachCall()
    {
        // EnvFlags must hold no cache: connectors flip these vars between crawls
        // in-process (and every test in this assembly depends on it). A static
        // memo added "for performance" would freeze the first value read in the
        // process and break both.
        using var env = new Scope((Flag, "true"), (Num, "1"));

        Assert.True(EnvFlags.IsTrue(Flag));
        Assert.Equal(1, EnvFlags.GetInt(Num, -1));

        env.Set(Flag, "false");
        env.Set(Num, "2");

        Assert.False(EnvFlags.IsTrue(Flag));
        Assert.True(EnvFlags.IsFalse(Flag));
        Assert.Equal(2, EnvFlags.GetInt(Num, -1));
    }

    [Fact]
    public void SettingAFlagToEmpty_ReadsAsAbsentOnEveryPlatform()
    {
        // "" and unset are NOT the same storage on both OSes, verified: on Unix
        // an empty value round-trips as "" (env vars live in a managed
        // dictionary), while Win32 SetEnvironmentVariableW drops the variable
        // entirely and the read comes back null. Hence IsNullOrEmpty rather than
        // Null -- what the chassis has to guarantee is that BOTH shapes are
        // treated as "operator said nothing", so an `ENV FLAG=` line in a
        // Dockerfile cannot mean one thing on a developer's Mac and another on a
        // Windows host.
        using var env = new Scope((Flag, ""));

        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Flag)));
        Assert.False(EnvFlags.IsTrue(Flag));
        Assert.False(EnvFlags.IsFalse(Flag));
        Assert.True(EnvFlags.IsTrueOrDefault(Flag, fallback: true));
        Assert.False(EnvFlags.IsTrueOrDefault(Flag, fallback: false));

        // Whitespace, by contrast, round-trips verbatim on both platforms, so
        // the blank-but-present branch of IsTrueOrDefault is reachable this way
        // everywhere -- which is why the blank tests above use " " and "\t".
        env.Set(Flag, " ");
        Assert.Equal(" ", Environment.GetEnvironmentVariable(Flag));
    }

    [Fact]
    public void ANullFlagNameThrowsRatherThanSilentlyDefaulting()
    {
        // No defensive null-check: a null name is a bug in the CALLER, and the
        // alternative -- swallowing it and returning the default -- would make a
        // gate that never turns on look like an operator configuration problem.
        Assert.ThrowsAny<ArgumentException>(() => EnvFlags.IsTrue(null!));
        Assert.ThrowsAny<ArgumentException>(() => EnvFlags.IsFalse(null!));
        Assert.ThrowsAny<ArgumentException>(() => EnvFlags.IsTrueOrDefault(null!, true));
        Assert.ThrowsAny<ArgumentException>(() => EnvFlags.GetInt(null!, 0));
    }

    // ------------------------------------------ IDENTITY_SYNC_ON_INCREMENTAL

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void IdentitySyncOnIncremental_DefaultsOn(string? raw)
    {
        // Default ON is a deliberate ACL-freshness choice: entitlements re-sync
        // every incremental crawl instead of once per full crawl. If this flips
        // to default-OFF, permission changes go stale for hours with no config
        // change and no log line to explain it.
        using var env = new Scope((IdentitySyncVar, raw));
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData(" YES ")]
    public void IdentitySyncOnIncremental_ExplicitOnStaysOn(string raw)
    {
        using var env = new Scope((IdentitySyncVar, raw));
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData(" no ")]
    public void IdentitySyncOnIncremental_ExplicitOffRestoresFullCrawlOnlyBehaviour(string raw)
    {
        using var env = new Scope((IdentitySyncVar, raw));
        Assert.False(EnvFlags.IdentitySyncOnIncremental);
    }

    [Theory]
    [InlineData("flase")]
    [InlineData("off")]
    [InlineData("disabled")]
    public void IdentitySyncOnIncremental_UnrecognisedValueLeavesItOn(string raw)
    {
        // Was the defect this test documented: the property read
        // IsTrueOrDefault(name, true), so a one-character typo reverted identity
        // sync to full-crawl cadence -- stretching ACL staleness from the
        // incremental cadence to hours -- while AltrataConnector, reading the
        // SAME variable as !EnvFlags.IsFalse, kept running it. Two consumers of
        // one env var, opposite answers.
        //
        // Now spelled !IsFalse, and IsTrueOrDefault would give the same answer
        // anyway. Only an explicit, recognised "false" turns it off.
        using var env = new Scope((IdentitySyncVar, raw));
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
    }

    [Fact]
    public void IdentitySyncOnIncremental_IsReadEveryTime()
    {
        using var env = new Scope((IdentitySyncVar, "false"));
        Assert.False(EnvFlags.IdentitySyncOnIncremental);

        env.Set(IdentitySyncVar, null);
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
    }
}
