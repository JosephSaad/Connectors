// AclEngine/IdentitySync.cs
// -------------------------
// Identity crawl for BDH: loads the Salesforce user directory from the BDH
// "User" object export (BDH_IDENTITY_OBJECT, default "User" — BDH mirrors the
// Salesforce User table nightly like every other object) and resolves each
// user to an Entra ID identity by email/UPN through Microsoft Graph,
// persisting the mapping in the identity store. Runs before every full
// content crawl (and on incrementals when IDENTITY_SYNC_ON_INCREMENTAL=true).
// identity-dry-run executes the same resolution without persisting (--save
// persists).

using HadoopConnector.Config;
using HadoopConnector.Graph;
using HadoopConnector.Hdfs;
using HadoopConnector.Infrastructure;

namespace HadoopConnector.AclEngine;

public sealed class IdentitySyncResult
{
    public int UsersTotal { get; set; }
    public int UsersResolved { get; set; }
    public int UsersWithoutEmail { get; set; }
    public List<string> UnresolvedEmails { get; } = new();
}

public sealed class IdentitySync
{
    private static readonly IAppLogger Logger = Logging.GetLogger("hadoop_connector.identity");

    private readonly BdhFetcher _fetcher;
    private readonly GraphClient _graph;
    private readonly AppConfig _config;

    public IdentitySync(BdhFetcher fetcher, GraphClient graph, AppConfig config)
    {
        _fetcher = fetcher;
        _graph = graph;
        _config = config;
    }

    /// <summary>Synthesized object config for the identity export (not part of
    /// the content schema — id/email/name columns only).</summary>
    internal ObjectConfig IdentityObjectConfig() => new()
    {
        ObjectName = _config.IdentityObject,
        DisplayName = "BDH User Directory",
        SelectedFields = new Dictionary<string, string>
        {
            ["Id"] = "Id",
            ["Email"] = "Email",
            ["Name"] = "Name",
            ["IsActive"] = "IsActive",
        },
    };

    // ── Directory load ───────────────────────────────────────────────────────

    /// <summary>Load the user directory from the BDH User export. The identity
    /// object bypasses the full-scan guard (it is a bounded directory read,
    /// still subject to the row cap and file-size bounds).</summary>
    public async Task<UserDirectory> LoadDirectoryAsync(CancellationToken ct = default)
    {
        var directory = new UserDirectory();
        var result = await _fetcher.FetchAsync(
                IdentityObjectConfig(), fullCrawl: true, sinceUtc: null, ct, bypassScanGuard: true)
            .ConfigureAwait(false);

        // A PARTIAL user export (row cap hit, or an oversize User file skipped)
        // yields a silently-incomplete directory: every user it omits then
        // resolves to a coarse/fallback ACL for the WHOLE crawl. That is an
        // integrity failure, not a warning — fail the sync loudly (and alert),
        // matching how id-less exports are refused. Raise BDH_MAX_RECORDS_PER_OBJECT
        // / BDH_MAX_FILE_BYTES for the identity object, or narrow it.
        if (result.Incomplete)
        {
            var reason = result.Truncated
                ? "row cap (BDH_MAX_RECORDS_PER_OBJECT)"
                : "an oversize file skipped (BDH_MAX_FILE_BYTES)";
            var message =
                $"Identity directory load for '{_config.IdentityObject}' is INCOMPLETE ({reason}) — "
                + $"only {result.Records.Count} user(s) read. Proceeding would apply coarse/fallback "
                + "ACLs across the entire crawl. Refusing the sync; raise the identity object's "
                + "row cap / file-size bound or narrow the export, then re-run.";
            Logger.Error(message);
            await Alerting.RaiseAsync(
                "identity_directory_incomplete",
                message,
                new Dictionary<string, object?>
                {
                    ["identityObject"] = _config.IdentityObject,
                    ["usersRead"] = result.Records.Count,
                    ["truncated"] = result.Truncated,
                    ["skippedOversize"] = result.SkippedOversize,
                }).ConfigureAwait(false);
            throw new InvalidDataException(message);
        }

        foreach (var record in result.Records)
        {
            var user = ParseUser(record);
            if (user is not null)
                directory.Add(user);
        }
        Logger.Info($"BDH user directory: {directory.Count} user(s) loaded.");
        return directory;
    }

    internal static BdhUser? ParseUser(BdhRecord record)
    {
        var id = record.RawId;
        if (string.IsNullOrEmpty(id))
            return null;
        var activeRaw = record.GetString("IsActive");
        var isActive = activeRaw is null
            || activeRaw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || activeRaw == "1";
        return new BdhUser(id, record.GetString("Email"), record.GetString("Name"), isActive);
    }

    // ── Entra resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolve every directory user to an Entra object id by email/UPN and
    /// persist mappings into <paramref name="store"/> (unless dry-run).
    /// </summary>
    public async Task<IdentitySyncResult> SyncAsync(
        UserDirectory directory,
        IIdentityStore store,
        bool persist = true,
        CancellationToken ct = default)
    {
        var result = new IdentitySyncResult { UsersTotal = directory.Count };

        foreach (var user in directory.Users.Values)
        {
            ct.ThrowIfCancellationRequested();
            string? entraId = null;
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                result.UsersWithoutEmail++;
            }
            else
            {
                entraId = await ResolveUserByEmailAsync(user.Email!, ct).ConfigureAwait(false);
                if (entraId is null)
                    result.UnresolvedEmails.Add(user.Email!);
                else
                    result.UsersResolved++;
            }
            if (persist)
            {
                store.Upsert(new PrincipalMapping(
                    user.Id, "user", user.Email, entraId, DateTime.UtcNow));
            }
        }

        Logger.Info(
            $"Identity sync: {result.UsersResolved}/{result.UsersTotal} users resolved to Entra; "
            + $"{result.UnresolvedEmails.Count} unresolved; {result.UsersWithoutEmail} without email.");
        return result;
    }

    /// <summary>Graph lookup: GET /users/{email}?$select=id → object id, null on 404.</summary>
    internal async Task<string?> ResolveUserByEmailAsync(string email, CancellationToken ct)
    {
        try
        {
            var response = await _graph.GetAsync(
                $"users/{Uri.EscapeDataString(email)}?$select=id", ct).ConfigureAwait(false);
            if (response.IsSuccess)
                return response.Body?["id"]?.GetValue<string>();
            return null;
        }
        catch (Exception exc)
        {
            Logger.Warning($"Entra lookup failed for '{email}': {exc.Message}");
            return null;
        }
    }
}
