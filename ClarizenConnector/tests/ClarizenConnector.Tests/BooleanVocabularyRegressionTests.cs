// BooleanVocabularyRegressionTests.cs
// -----------------------------------
// Pins the defects closed when this connector stopped carrying its own EnvFlags.
//
// The chassis fixed its boolean vocabulary in "EnvFlags: one boolean vocabulary,
// and a typo can no longer flip a gate" — trim always, and treat an unrecognised
// value as ABSENT so the declared default wins. That fix landed in
// Connector.Chassis only. This connector kept a local EnvFlags holding the
// pre-fix parser (no trim, unrecognised -> false), so it never received it: the
// two failures that commit reproduced were still live here.
//
// Both are asserted below through the real call sites, not through the parser,
// because the parser was only half of each defect — a default-ON gate spelled
// `blank-or-IsTrue` stays broken no matter how well the parser behaves.

using ClarizenConnector.Infrastructure;
using ClarizenConnector.Webhook;

namespace ClarizenConnector.Tests;

public class BooleanVocabularyRegressionTests
{
    // ── CLASSIFICATION_ENFORCE_ACL: a trailing space silently disabled it ────
    //
    // "true " is what a .env line or a folded YAML scalar produces. The old
    // parser did not trim, so it read as OFF — and because ValidateConfig reads
    // the same flag through the same parser, validate-config reported success
    // while Restricted items were indexed without the enforcement-group lock.

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
        // Default-OFF gate: only an explicit truthy value turns it on, so an
        // unrecognised value must NOT enable ACL enforcement either.
        using var env = new EnvScope(("CLASSIFICATION_ENFORCE_ACL", raw));
        Assert.False(EnvFlags.IsTrue("CLASSIFICATION_ENFORCE_ACL"));
    }

    // ── CIRCUIT_BREAKER: an unrecognised value left the breakers open ────────
    //
    // A protective default-ON gate. `CircuitBreakerEnabledFromEnv` read it as
    // `blank-or-IsTrue`, so CIRCUIT_BREAKER=on — not blank, not truthy — put
    // every breaker into passthrough while the summary on that same method said
    // "only an explicit false disables it".

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

    // ── The same shape, in the other protective defaults ─────────────────────

    // The env var names come from the consts the production code reads, not from
    // string literals here: a literal that does not match sets an unrelated
    // variable, leaves the gate at its default, and makes a default-ON assertion
    // pass while testing nothing.

    [Theory]
    [InlineData("on")]
    [InlineData("ture")]
    [InlineData(null)]
    public void WebhookTimestampRequirement_SurvivesAnUnrecognisedValue(string? raw)
    {
        // Signed-timestamp anti-replay. A value nobody meant must not be what
        // switches replay protection off.
        using var env = new EnvScope((WebhookAuthenticator.RequireTimestampEnvVar, raw));
        Assert.True(WebhookAuthenticator.FromEnv("secret").RequireTimestamp);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("ture")]
    [InlineData(null)]
    public void DecisionLedger_SurvivesAnUnrecognisedValue(string? raw)
    {
        using var env = new EnvScope((DecisionLedger.EnvVar, raw));
        Assert.True(DecisionLedger.Enabled);
    }

    [Fact]
    public void EveryProtectiveDefault_IsStillTurnedOffByAnExplicitFalse()
    {
        // The fix must not have made these gates impossible to disable. This is
        // the assertion that caught the mismatched literal described above.
        using var env = new EnvScope(
            (WebhookAuthenticator.RequireTimestampEnvVar, "false"),
            (DecisionLedger.EnvVar, "false"),
            ("CIRCUIT_BREAKER", "false"));
        Assert.False(WebhookAuthenticator.FromEnv("secret").RequireTimestamp);
        Assert.False(DecisionLedger.Enabled);
        Assert.False(CircuitBreakerOptions.CircuitBreakerEnabledFromEnv());
    }
}
