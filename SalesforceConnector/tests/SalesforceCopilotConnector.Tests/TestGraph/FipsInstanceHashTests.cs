// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// WP-SF-5 — FIPS 140-3 posture: the identity-critical instance-key hash.
//
// `InstanceHash` derives the 8-hex `instance_hash` / `@InstanceHash` primary-key
// component of the field cache from the Salesforce instance URL. It used to be an
// MD5 prefix; it is now a SHA-256 prefix. Two separate implementations exist
// (SQLite `IdentityStore`, SQL Server `SqlServerIdentityStore`) and they must not
// drift, so every property below is asserted against both.
//
// The output *shape* is load-bearing and deliberately unchanged: exactly 8
// lowercase hex characters, which keeps `dbo.FieldCache.InstanceHash
// nvarchar(16)` and both primary keys valid with no DDL change.
//
// Offline by construction: `SqlServerIdentityStore.InstanceHash` is static, so
// these run with no SQL Server present.

using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SalesforceCopilotConnector.Graph;

namespace SalesforceCopilotConnector.Tests.TestGraph;

public class FipsInstanceHashTests
{
    // Pinned vectors: first 8 chars of the lowercase hex SHA-256 of the UTF-8 URL.
    //   printf '%s' 'https://myorg.my.salesforce.com' | shasum -a 256
    // The corresponding MD5 prefixes are listed so the divergence is explicit —
    // these tests fail against the pre-WP-SF-5 MD5 implementation.
    public const string PinnedUrl = "https://myorg.my.salesforce.com";
    public const string PinnedSha256Prefix = "5419f459";
    public const string PinnedLegacyMd5Prefix = "91d25d4b";

    public static TheoryData<string, string> Vectors => new()
    {
        { "https://myorg.my.salesforce.com", "5419f459" },
        { "https://org1.salesforce.com", "ed35a6fc" },
        { "https://org2.salesforce.com", "933d4f6d" },
    };

    // ── 1. SHA-256 derived (fails against MD5) ──────────────────────────────────

    [Theory]
    [MemberData(nameof(Vectors))]
    public void IdentityStoreInstanceHashIsSha256Derived(string url, string expected)
    {
        Assert.Equal(expected, IdentityStore.InstanceHash(url));
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SqlServerIdentityStoreInstanceHashIsSha256Derived(string url, string expected)
    {
        Assert.Equal(expected, SqlServerIdentityStore.InstanceHash(url));
    }

    [Fact]
    public void InstanceHashIsNotTheLegacyMd5Value()
    {
        Assert.NotEqual(PinnedLegacyMd5Prefix, IdentityStore.InstanceHash(PinnedUrl));
        Assert.NotEqual(PinnedLegacyMd5Prefix, SqlServerIdentityStore.InstanceHash(PinnedUrl));
    }

    // ── 2. Shape invariant: exactly 8 lowercase hex chars ───────────────────────

    [Theory]
    [InlineData("https://myorg.my.salesforce.com")]
    [InlineData("https://org1.salesforce.com")]
    [InlineData("https://test.my.salesforce.com/services/data/v59.0")]
    [InlineData("")]
    [InlineData("https://ünïcode.my.salesforce.com")]
    [InlineData("HTTPS://UPPER.MY.SALESFORCE.COM")]
    public void ShapeIsEightLowercaseHexChars(string url)
    {
        foreach (var value in new[] { IdentityStore.InstanceHash(url), SqlServerIdentityStore.InstanceHash(url) })
        {
            Assert.Equal(8, value.Length);
            Assert.Matches("^[0-9a-f]{8}$", value);
        }
    }

    [Fact]
    public void ShapeFitsTheSqlServerColumnWidth()
    {
        // dbo.FieldCache.InstanceHash is nvarchar(16) — the value must fit with no
        // DDL change. Guards against a future "just use the full digest" edit.
        Assert.True(IdentityStore.InstanceHash(PinnedUrl).Length <= 16);
        Assert.True(SqlServerIdentityStore.InstanceHash(PinnedUrl).Length <= 16);
    }

    // ── 3. The two implementations must not drift ───────────────────────────────

    [Theory]
    [InlineData("https://myorg.my.salesforce.com")]
    [InlineData("https://org1.salesforce.com")]
    [InlineData("https://org2.salesforce.com")]
    [InlineData("https://sandbox--dev.sandbox.my.salesforce.com")]
    [InlineData("")]
    public void BothStoresAgree(string url)
    {
        Assert.Equal(IdentityStore.InstanceHash(url), SqlServerIdentityStore.InstanceHash(url));
    }

    [Fact]
    public void DistinctInstancesGetDistinctKeys()
    {
        Assert.NotEqual(
            IdentityStore.InstanceHash("https://org1.salesforce.com"),
            IdentityStore.InstanceHash("https://org2.salesforce.com"));
    }
}

// ── 4. Upgrade behavior: orphaned legacy rows are LEFT ALONE (option (a)) ───────

/// <summary>
/// WP-SF-5 chose to leave pre-upgrade MD5-keyed `field_cache` rows in place rather
/// than auto-clearing them: the field cache is a pure cache (a miss re-runs the
/// existing INVALID_FIELD discovery loop), a single database may legitimately hold
/// rows for several Salesforce instances (sandbox + production), and there is no
/// safe way to tell an orphan from another live instance's row without recomputing
/// the retired MD5 key. These tests pin that documented no-op, and pin the operator
/// cleanup path (`ClearFieldCache()`) that reclaims the orphans on demand.
/// </summary>
public class FipsLegacyFieldCacheRowTests : IdentityStoreTestBase
{
    private string DbPath => Path.Combine(TmpPath, "test_identity.db");

    /// <summary>Insert a row keyed by the retired MD5 value, as an upgraded DB would hold.</summary>
    private void InsertLegacyRow(string objectType, string legacyHash)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO field_cache (object_type, instance_hash, fields, cached_at) VALUES (@t, @h, @f, @c)";
        cmd.Parameters.AddWithValue("@t", objectType);
        cmd.Parameters.AddWithValue("@h", legacyHash);
        cmd.Parameters.AddWithValue("@f", "[\"Id\", \"LegacyField\"]");
        cmd.Parameters.AddWithValue("@c", "2026-01-01T00:00:00+00:00");
        cmd.ExecuteNonQuery();
    }

    private int RowCount()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM field_cache";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void LegacyRowIsNotReadableUnderTheNewKey()
    {
        InsertLegacyRow("Account", FipsInstanceHashTests.PinnedLegacyMd5Prefix);
        // Cache miss, not a stale hit — the caller re-runs field discovery.
        Assert.Null(Store.GetCachedFields(FipsInstanceHashTests.PinnedUrl, "Account"));
    }

    [Fact]
    public void LegacyRowSurvivesStoreOpenAndReopen()
    {
        InsertLegacyRow("Account", FipsInstanceHashTests.PinnedLegacyMd5Prefix);
        Assert.Equal(1, RowCount());

        // Re-opening runs InitSchema + Migrate again. Option (a) is a no-op there:
        // nothing may be deleted on start, on this open or any later one.
        for (var i = 0; i < 3; i++)
        {
            using var reopened = new IdentityStore(DbPath, "test-conn");
            Assert.Equal(1, RowCount());
        }
    }

    [Fact]
    public void RediscoveryWritesANewRowBesideTheOrphan()
    {
        InsertLegacyRow("Account", FipsInstanceHashTests.PinnedLegacyMd5Prefix);
        Store.SaveCachedFields(FipsInstanceHashTests.PinnedUrl, "Account", new[] { "Id", "Name" });

        // Orphan + rebuilt row coexist; the PK (object_type, instance_hash) makes
        // that legal because the hash component differs.
        Assert.Equal(2, RowCount());
        Assert.Equal(new[] { "Id", "Name" }, Store.GetCachedFields(FipsInstanceHashTests.PinnedUrl, "Account"));
    }

    [Fact]
    public void OtherInstancesLiveRowsAreUntouched()
    {
        // The reason a blanket "delete rows whose hash != current" is forbidden:
        // one DB legitimately holds sandbox + production rows.
        const string sandbox = "https://sandbox--dev.sandbox.my.salesforce.com";
        Store.SaveCachedFields(sandbox, "Account", new[] { "Id", "SandboxOnly" });
        InsertLegacyRow("Lead", FipsInstanceHashTests.PinnedLegacyMd5Prefix);

        using (var reopened = new IdentityStore(DbPath, "test-conn"))
        {
            Assert.Equal(new[] { "Id", "SandboxOnly" }, reopened.GetCachedFields(sandbox, "Account"));
        }
        Assert.Equal(2, RowCount());
    }

    [Fact]
    public void OperatorCleanupReclaimsOrphansViaClearFieldCache()
    {
        // The documented one-time cleanup: the existing no-argument ClearFieldCache
        // truncates the table, orphans included. Safe because it is a pure cache.
        InsertLegacyRow("Account", FipsInstanceHashTests.PinnedLegacyMd5Prefix);
        InsertLegacyRow("Lead", FipsInstanceHashTests.PinnedLegacyMd5Prefix);
        Store.SaveCachedFields(FipsInstanceHashTests.PinnedUrl, "Account", new[] { "Id", "Name" });

        var deleted = Store.ClearFieldCache();

        Assert.Equal(3, deleted);
        Assert.Equal(0, RowCount());
        Assert.Null(Store.GetCachedFields(FipsInstanceHashTests.PinnedUrl, "Account"));
    }

    [Fact]
    public void PerInstanceClearDoesNotTouchTheOrphan()
    {
        // ClearFieldCache(instanceUrl) keys off the *current* algorithm, so it
        // cannot reach a legacy row — operators must use the no-arg form.
        InsertLegacyRow("Account", FipsInstanceHashTests.PinnedLegacyMd5Prefix);
        Store.SaveCachedFields(FipsInstanceHashTests.PinnedUrl, "Account", new[] { "Id", "Name" });

        var deleted = Store.ClearFieldCache(FipsInstanceHashTests.PinnedUrl);

        Assert.Equal(1, deleted);
        Assert.Equal(1, RowCount());
    }
}

// ── 5. Source contract: no MD5 / SHA-1 left under src/ ─────────────────────────

/// <summary>
/// Grep-as-a-test, modelled on <see cref="SqlScriptValidationTests"/>'s repo-file
/// checks: the FIPS posture documented in SECURITY.md and docs/THREAT_MODEL.md is
/// only true as long as no broken primitive comes back. Read-only over repo files.
/// </summary>
public class FipsSourceContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("SalesforceCopilotConnector.sln").Length == 0)
            dir = dir.Parent;
        Assert.True(dir != null, "could not locate repo root (SalesforceCopilotConnector.sln) above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles()
    {
        var src = Path.Combine(RepoRoot(), "src", "SalesforceCopilotConnector");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"\bMD5\s*\.")]
    [InlineData(@"\bSHA1\s*\.")]
    [InlineData(@"\bMD5CryptoServiceProvider\b")]
    [InlineData(@"\bSHA1CryptoServiceProvider\b")]
    [InlineData(@"HashAlgorithmName\s*\.\s*(MD5|SHA1)\b")]
    [InlineData(@"CreateHMAC\s*\(\s*""(MD5|SHA1)""")]
    public void NoBrokenHashPrimitiveUnderSrc(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
        var hits = new List<string>();
        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                    hits.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(
            hits.Count == 0,
            $"FIPS contract violated — /{pattern}/ found under src/:\n  " + string.Join("\n  ", hits));
    }

    [Fact]
    public void SourceFileSweepActuallyScannedFiles()
    {
        // Guard the guard: a broken RepoRoot/glob would make the checks above pass
        // vacuously.
        Assert.True(SourceFiles().Count() > 50, "source sweep found suspiciously few .cs files");
    }

    [Fact]
    public void InstanceHashUsesSha256InBothStores()
    {
        var src = Path.Combine(RepoRoot(), "src", "SalesforceCopilotConnector", "Graph");
        foreach (var name in new[] { "IdentityStore.cs", "SqlServerIdentityStore.cs" })
        {
            var text = File.ReadAllText(Path.Combine(src, name));
            Assert.Contains("SHA256.HashData", text, StringComparison.Ordinal);
        }
    }
}
