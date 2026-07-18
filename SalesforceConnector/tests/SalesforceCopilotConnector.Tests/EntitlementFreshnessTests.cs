// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// EntitlementFreshnessTests.cs
// ----------------------------
// #5 — IDENTITY_SYNC_ON_INCREMENTAL now defaults ON (opt-out), so entitlement
// re-sync runs on every incremental crawl and the freshness lag shrinks to the
// incremental cadence. These pin the new default and the opt-out semantics.

using SalesforceCopilotConnector.Infrastructure;

namespace SalesforceCopilotConnector.Tests;

[Collection("EnvVars")]
public sealed class EntitlementFreshnessTests : IDisposable
{
    private readonly string? _saved =
        Environment.GetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL");

    public void Dispose() =>
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", _saved);

    [Fact]
    public void IdentitySyncOnIncremental_DefaultsOn_WhenUnset()
    {
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", null);
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", "");
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("FALSE")]
    [InlineData("No")]
    public void IdentitySyncOnIncremental_Disabled_ByFalseyValue(string value)
    {
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", value);
        Assert.False(EnvFlags.IdentitySyncOnIncremental);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("anything-else")]
    public void IdentitySyncOnIncremental_On_ForNonFalseyValue(string value)
    {
        Environment.SetEnvironmentVariable("IDENTITY_SYNC_ON_INCREMENTAL", value);
        Assert.True(EnvFlags.IdentitySyncOnIncremental);
    }
}
