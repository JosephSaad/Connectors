// EnvFlagsSemanticsTests.cs
// -------------------------
// Pins the distinction between the chassis's two default-ON helpers.
//
// When this connector moved onto the chassis SecretProvider it lost its local
// EnvFlags, which lived in the same file. The chassis had IsTrueOrDefault but
// not IsFalse, and the tempting rewrite -- !IsFalse(x) becomes
// IsTrueOrDefault(x, true) -- is NOT equivalent. They agree on unset, on "true"
// and on "false"; they disagree on anything unrecognised, which is exactly what
// a typo produces. For CIRCUIT_BREAKER that difference decides whether a
// mistyped environment variable silently disables a protective default.

namespace AltrataConnector.Tests;

public class EnvFlagsSemanticsTests : IDisposable
{
    private const string Flag = "ALTRATA_ENVFLAGS_SEMANTICS_PROBE";

    public void Dispose() => Environment.SetEnvironmentVariable(Flag, null);

    [Theory]
    [InlineData(null)]      // unset
    [InlineData("")]        // blank
    [InlineData("   ")]
    public void DefaultOnFlag_StaysOnWhenUnset(string? raw)
    {
        Environment.SetEnvironmentVariable(Flag, raw);
        Assert.True(!EnvFlags.IsFalse(Flag));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData(" No ")]
    public void DefaultOnFlag_TurnsOffOnlyForARecognisedOff(string raw)
    {
        Environment.SetEnvironmentVariable(Flag, raw);
        Assert.False(!EnvFlags.IsFalse(Flag));
    }

    /// <summary>
    /// The reason IsFalse exists rather than being folded into IsTrueOrDefault.
    /// A protective default must not be switched off by a value nobody meant.
    /// </summary>
    [Theory]
    [InlineData("maybe")]
    [InlineData("flase")]   // the actual typo this guards against
    [InlineData("off")]     // plausible, but not in the recognised vocabulary
    [InlineData("1")]
    public void UnrecognisedValue_LeavesADefaultOnFlagON_UnlikeIsTrueOrDefault(string raw)
    {
        Environment.SetEnvironmentVariable(Flag, raw);

        // What the code actually uses, e.g. CircuitBreakerEnabled.
        Assert.True(!EnvFlags.IsFalse(Flag));

        // What a careless consolidation would have substituted. Documented here
        // so the divergence is visible rather than surprising: for everything
        // except "1", this is the OPPOSITE answer.
        if (raw != "1")
        {
            Assert.False(EnvFlags.IsTrueOrDefault(Flag, fallback: true));
        }
    }
}
