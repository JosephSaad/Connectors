// BooleanVocabularyRegressionTests.cs
// -----------------------------------
// Pins the defects closed when this connector stopped carrying its own EnvFlags.
//
// The chassis fixed its boolean vocabulary in "EnvFlags: one boolean vocabulary,
// and a typo can no longer flip a gate" — trim always, and treat an unrecognised
// value as ABSENT so the declared default wins. That fix landed in
// Connector.Chassis only. This connector kept a local EnvFlags holding the
// pre-fix parser (no trim, unrecognised -> false), so it never received it.
//
// Asserted through the real call sites rather than the parser, because the
// parser was only half of each defect: a default-ON gate spelled
// `blank-or-IsTrue` stays broken however well the parser behaves.

using HadoopConnector.Infrastructure;

namespace HadoopConnector.Tests;

public class BooleanVocabularyRegressionTests
{
    // ── CLASSIFICATION_ENFORCE_ACL: a trailing space silently disabled it ────
    //
    // "true " is what a .env line or a folded YAML scalar produces. The old
    // parser did not trim, so it read as OFF — and ValidateConfig reads the same
    // flag through the same parser, so validate-config reported success while
    // Restricted items were indexed without the enforcement-group lock.

    [Theory]
    [InlineData("true ")]
    [InlineData(" true")]
    [InlineData("\ttrue\t")]
    [InlineData(" TRUE ")]
    [InlineData(" 1 ")]
    [InlineData(" yes ")]
    public void ClassificationEnforceAcl_IsNotDisabledBySurroundingWhitespace(string raw)
    {
        using var env = new EnvScope(("CLASSIFICATION_ENFORCE_ACL", raw));
        Assert.True(EnvFlags.IsTrue("CLASSIFICATION_ENFORCE_ACL"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData(" no ")]
    [InlineData("0")]
    [InlineData(null)]
    [InlineData("")]
    public void ClassificationEnforceAcl_StaysOffWhenUnsetOrExplicitlyFalse(string? raw)
    {
        // Default-OFF gate: an unrecognised value must not enable enforcement
        // either — "absent" means the declared default, in both directions.
        using var env = new EnvScope(("CLASSIFICATION_ENFORCE_ACL", raw));
        Assert.False(EnvFlags.IsTrue("CLASSIFICATION_ENFORCE_ACL"));
    }

    // ── CIRCUIT_BREAKER: an unrecognised value left the breakers open ────────
    //
    // A protective default-ON gate whose own summary said "only an explicit
    // false disables it", while the code read it as `blank-or-IsTrue` — so
    // CIRCUIT_BREAKER=on put the Hdfs and Graph breakers into passthrough.

    [Theory]
    [InlineData("on")]
    [InlineData("enabled")]
    [InlineData("ture")]
    [InlineData("Y")]
    [InlineData("2")]
    [InlineData("true ")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CircuitBreaker_StaysEnabledUnlessExplicitlyTurnedOff(string? raw)
    {
        using var env = new EnvScope(("CIRCUIT_BREAKER", raw));
        Assert.True(CircuitBreakerOptions.CircuitBreakerEnabledFromEnv());
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    [InlineData("0")]
    [InlineData("no")]
    public void CircuitBreaker_IsDisabledOnlyByAnExplicitFalsyValue(string raw)
    {
        using var env = new EnvScope(("CIRCUIT_BREAKER", raw));
        Assert.False(CircuitBreakerOptions.CircuitBreakerEnabledFromEnv());
    }

    // ── The decision ledger is the compliance record: same protective shape ──

    [Theory]
    [InlineData("on")]
    [InlineData("ture")]
    [InlineData(null)]
    public void DecisionLedger_SurvivesAnUnrecognisedValue(string? raw)
    {
        using var env = new EnvScope(("DECISION_LEDGER", raw));
        Assert.True(DecisionLedger.Enabled);
    }

    [Fact]
    public void EveryProtectiveDefault_IsStillTurnedOffByAnExplicitFalse()
    {
        // The fix must not have made these gates impossible to disable.
        using var env = new EnvScope(
            ("DECISION_LEDGER", "false"),
            ("CIRCUIT_BREAKER", "false"));
        Assert.False(DecisionLedger.Enabled);
        Assert.False(CircuitBreakerOptions.CircuitBreakerEnabledFromEnv());
    }

    // ── ALLOW_FULL_SCAN stays default-OFF ────────────────────────────────────

    [Theory]
    [InlineData("ture")]
    [InlineData("on")]
    [InlineData(null)]
    [InlineData("")]
    public void AllowFullScan_IsNotEnabledByAnUnrecognisedValue(string? raw)
    {
        // The fail-closed scale guard over a 150M+ row mart. TENANT_GOVERNANCE.md
        // treats any use of this as a capacity-plan change, so a typo must never
        // be what opens it.
        using var env = new EnvScope(("ALLOW_FULL_SCAN", raw));
        Assert.False(EnvFlags.IsTrue("ALLOW_FULL_SCAN"));
    }
}
