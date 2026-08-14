// EnvFlagsSemanticsTests.cs
// -------------------------
// Pins how a default-ON flag behaves when read through the chassis, from the
// connector that binds Connector.Chassis.EnvFlags by alias.
//
// HISTORY, because this file used to assert the opposite. When this connector
// moved onto the chassis it lost its local EnvFlags. The chassis had
// IsTrueOrDefault but not IsFalse, and the two were NOT equivalent on an
// unrecognised value -- IsTrueOrDefault(x, true) returned false for a typo.
// This file pinned that divergence as deliberate, on the reasoning that IsFalse
// existed precisely because of it.
//
// The divergence was the defect, not the design. IsTrueOrDefault was breaking
// its own contract: asked for the value OR the default, a typo got neither. The
// chassis now treats an unrecognised value as absent everywhere -- every caller
// falls back to its own declared default and a warning names the variable -- so
// the two idioms agree, and a value nobody meant can no longer flip a gate in
// either direction. Both helpers are kept because they read differently at a
// call site, not because they answer differently.

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
    /// A protective default must not be switched off by a value nobody meant --
    /// and now BOTH spellings of "default ON" honour that, where once only
    /// IsFalse did.
    /// </summary>
    [Theory]
    [InlineData("maybe")]
    [InlineData("flase")]   // the actual typo this guards against
    [InlineData("off")]     // plausible, but not in the recognised vocabulary
    [InlineData("1")]
    public void UnrecognisedValue_LeavesADefaultOnFlagON(string raw)
    {
        Environment.SetEnvironmentVariable(Flag, raw);

        // What the code actually writes, e.g. CircuitBreakerEnabled.
        Assert.True(!EnvFlags.IsFalse(Flag));

        // The other spelling. This assertion is the inverse of what this file
        // asserted before the parsers were consolidated: it used to be
        // Assert.False, and that difference was a real defect -- it is what let
        // CIRCUIT_BREAKER=on ship a connector with no breaker.
        Assert.True(EnvFlags.IsTrueOrDefault(Flag, fallback: true));
    }

    [Theory]
    [InlineData("true ")]
    [InlineData(" true")]
    [InlineData("\tYES\n")]
    public void PaddingIsDeploymentPlumbing_NotIntent(string raw)
    {
        // The half of the fix this connector felt most directly: IsTrue did not
        // trim, so CLASSIFICATION_ENFORCE_ACL="true " read as OFF *and*
        // suppressed the AppConfig.Validate() guard that requires
        // CLASSIFICATION_ENFORCE_GROUP_ID -- ACL enforcement silently off with
        // validate-config reporting success. A trailing space in a .env line or
        // a folded YAML scalar was enough.
        Environment.SetEnvironmentVariable(Flag, raw);

        Assert.True(EnvFlags.IsTrue(Flag));
        Assert.False(EnvFlags.IsFalse(Flag));
    }
}
