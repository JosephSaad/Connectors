// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Infrastructure/EnvFlags.cs
// --------------------------
// Command-layer feature gates read from environment variables. Uses the same
// truthy semantics as Salesforce.Settings.BoolEnv (true / 1 / yes, case-insensitive)
// so behavior is consistent across the codebase. Every flag defaults to OFF, so an
// unset environment reproduces the original single-node, file-backed behavior exactly.

namespace SalesforceCopilotConnector.Infrastructure;

/// <summary>Boolean feature flags read from the environment for the command layer.</summary>
public static class EnvFlags
{
    /// <summary>True when the named env var is <c>true</c>/<c>1</c>/<c>yes</c> (case-insensitive).</summary>
    public static bool IsTrue(string name)
    {
        var value = (Environment.GetEnvironmentVariable(name) ?? "false").ToLowerInvariant();
        return value is "true" or "1" or "yes";
    }

    /// <summary>
    /// <c>IDENTITY_SYNC_ON_INCREMENTAL</c> — run the (incremental) identity crawl on
    /// incremental content crawls too, not just full crawls. Default false.
    /// </summary>
    public static bool IdentitySyncOnIncremental => IsTrue("IDENTITY_SYNC_ON_INCREMENTAL");
}
