// Commands/CommandRegistry.cs
// ---------------------------
// All CLI commands and their handlers. See `guide` (or README.md) for usage.

using System.Text.Json;
using AltrataConnector.Altrata;
using AltrataConnector.Config;
using AltrataConnector.Entitlement;
using AltrataConnector.Graph;
using AltrataConnector.Infrastructure;
using AltrataConnector.State;

namespace AltrataConnector.Commands;

public static class CommandRegistry
{
    private static readonly IAppLogger Logger = Logging.GetLogger("altrata_connector");

    public static CliParser BuildParser()
    {
        var parser = new CliParser("altrata-connector");

        parser.AddCommand(new CommandSpec
        {
            Name = "guide",
            Description = "Show the complete setup and usage guide",
            Handler = args => Guide.RunAsync(args),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "setup-connection",
            Description = "Create/verify the Graph connection and register the schema (no ingestion)",
            Handler = WithRuntime("setup", async (runtime, _) =>
            {
                await SetupConnectionsAsync(runtime);
                Console.WriteLine("Connection(s) ready and schema registered.");
                return (object?)true;
            }),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "full-deployment",
            Description = "Deploy connection → schema → full crawl of the feed directory (with ACLs)",
            Options = ContinuousOptions(),
            Handler = WithRuntime("full-deployment", async (runtime, args) =>
            {
                await SetupConnectionsAsync(runtime);
                return await RunCrawlsAsync(runtime, args);
            }),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "ingest",
            Description = "Ingest feed deliveries only (connection & schema must already exist)",
            Options = ContinuousOptions(new OptionSpec
            {
                Name = "--incremental",
                Help = "Process only deliveries not yet in the processed ledger",
            }),
            Handler = WithRuntime("ingest", (runtime, args) => RunCrawlsAsync(runtime, args)),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "ingest-object",
            Description = "Ingest all records of one dataset (PersonProfile, Organization, ...)",
            Options = new List<OptionSpec>
            {
                new() { Name = "--type", HasValue = true, Required = true,
                        Help = $"Dataset: {string.Join(", ", Datasets.All)}" },
            },
            Handler = WithRuntime("ingest-object", async (runtime, args) =>
            {
                var dataset = Datasets.Canonical(args.GetString("--type")!);
                var result = await RunOneCrawlAsync(runtime, CrawlKind.Full, new[] { dataset });
                PrintCrawlResult(result);
                return !result.Stopped && result.DeliveriesRejected == 0;
            }),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "ingest-item",
            Description = "Fetch one person profile from the Altrata API (billable) and ingest it",
            Options = new List<OptionSpec>
            {
                new() { Name = "--id", HasValue = true, Required = true, Help = "Altrata person id" },
                new() { Name = "--purpose", HasValue = true, Required = true,
                        Help = "Purpose-of-use string recorded in the audit log" },
                new() { Name = "--requested-by", HasValue = true,
                        Help = "Actor recorded in the audit log (default: OS user)" },
            },
            Handler = WithRuntime("ingest-item", async (runtime, args) =>
            {
                var id = args.GetString("--id")!;
                var purpose = args.GetString("--purpose")!;
                var actor = args.GetString("--requested-by") ?? Environment.UserName;

                var seatSync = runtime.Seats.SyncSeats();
                var acl = SeatAclBuilder.BuildAcl(seatSync.Seats);

                var api = new AltrataApiClient(runtime.Config, runtime.State, runtime.Audit,
                    breaker: runtime.ApiBreaker);
                var record = await api.LookupPersonAsync(id, actor, purpose, ServiceStop.Token);

                var fuzzy = new Identity.FuzzyOptions
                {
                    Enabled = runtime.Config.EntityFuzzyMatching,
                    Threshold = runtime.Config.EntityMatchThreshold,
                    ReviewFloor = runtime.Config.EntityReviewFloor,
                    Review = new Identity.MatchReviewQueue(runtime.Config.ConnectorId),
                };
                var transformer = new ItemTransformer(
                    new Identity.EntityResolver(runtime.Identity, fuzzy),
                    classification: new ClassificationOptions
                    {
                        Enabled = runtime.Config.Classification,
                        Residency = runtime.Config.DataResidency,
                    });
                var item = transformer.Transform(record, acl);
                await runtime.Graph.PutItemAsync(item, ServiceStop.Token);
                runtime.Identity.RecordIngestedItem(new Identity.IngestedItem(
                    item.Id, record.Dataset, seatSync.SeatHash, DateTime.UtcNow));
                Metrics.Increment("altrata_items_ingested_total");

                Console.WriteLine($"Ingested {item.Id} (billable lookups to date: {runtime.State.GetBillableLookupCount()})");
                return (object?)true;
            }),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "retry-failed",
            Description = "Replay the dead-letter queue against Microsoft Graph",
            Options = new List<OptionSpec>
            {
                new() { Name = "--clear-on-success", Help = "Delete the dead-letter file when everything replays" },
                new() { Name = "--retire-unreplayable", Help = "Drop transform-failure entries (no payload to replay; fix the feed and re-ingest instead)" },
            },
            Handler = WithRuntime("retry-failed", (runtime, args) =>
                RetryFailedAsync(runtime, args.GetFlag("--clear-on-success"),
                    args.GetFlag("--retire-unreplayable"))),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "identity-dry-run",
            Description = "Preview the seat list, CRM contact load and crosswalk without touching ACLs",
            Options = new List<OptionSpec>
            {
                new() { Name = "--save", Help = "Persist the loaded seats/contacts into the identity store" },
            },
            Handler = WithRuntime("identity-dry-run", (runtime, args) =>
                IdentityDryRunAsync(runtime, args.GetFlag("--save"))),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "seat-sync",
            Description = "Refresh the seat list; a changed list triggers the re-ACL pass over all items",
            Handler = WithRuntime("seat-sync", async (runtime, _) =>
            {
                if (ShardingConfig.TryLoad(out var shards, out var shardError))
                {
                    // Each shard connection carries its own seat hash and item
                    // registry — sync (and re-ACL) every one.
                    foreach (var shard in shards)
                    {
                        Console.WriteLine($"[shard {shard.ConnectionId}]");
                        using var shardRuntime = runtime.CreateForShard(shard);
                        await SeatSyncOneAsync(shardRuntime);
                    }
                    return (object?)true;
                }
                if (shardError != null)
                    throw new ConfigurationError(shardError);
                await SeatSyncOneAsync(runtime);
                return (object?)true;
            }),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "validate-config",
            Description = "Preflight: env vars, config files, feed path, Graph/API connectivity",
            Options = new List<OptionSpec>
            {
                new() { Name = "--strict", Help = "Fail (exit 1) on connectivity warnings too" },
            },
            Handler = args => ValidateConfig.RunAsync(args.GetFlag("--strict")),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "forget-subject",
            Description = "Right-to-erasure (DSAR): withdraw every item for a person, suppress re-ingestion, ledger it",
            Options = new List<OptionSpec>
            {
                new() { Name = "--id", HasValue = true, Help = "Altrata person id to erase" },
                new() { Name = "--email", HasValue = true, Help = "Erase the person linked to this email (via the crosswalk)" },
                new() { Name = "--requested-by", HasValue = true, Help = "Actor recorded in the erasure ledger (default: OS user)" },
                new() { Name = "--confirm", Help = "Actually erase (without this flag a dry-run report is printed)" },
                new() { Name = "--dry-run", Help = "Force a dry-run report (default when --confirm is absent)" },
            },
            Handler = WithRuntime("forget-subject", (runtime, args) =>
                ForgetSubjectAsync(runtime, args.GetString("--id"), args.GetString("--email"),
                    args.GetString("--requested-by") ?? Environment.UserName,
                    confirm: args.GetFlag("--confirm") && !args.GetFlag("--dry-run"))),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "unsuppress-subject",
            Description = "Lift an erasure suppression so the subject may be ingested again",
            Options = new List<OptionSpec>
            {
                new() { Name = "--id", HasValue = true, Required = true, Help = "Altrata person id to un-suppress" },
                new() { Name = "--requested-by", HasValue = true, Help = "Actor recorded in the erasure ledger (default: OS user)" },
                new() { Name = "--confirm", Help = "Actually un-suppress (without this flag a dry-run is printed)" },
            },
            Handler = WithRuntime("unsuppress-subject", (runtime, args) =>
                UnsuppressSubjectAsync(runtime, args.GetString("--id")!,
                    args.GetString("--requested-by") ?? Environment.UserName,
                    confirm: args.GetFlag("--confirm"))),
        });

        parser.AddCommand(new CommandSpec
        {
            Name = "purge-all",
            Description = "License-end purge: withdraw every ingested item and wipe local state",
            Options = new List<OptionSpec>
            {
                new() { Name = "--confirm", Help = "Actually purge (without this flag a dry-run report is printed)" },
                new() { Name = "--dry-run", Help = "Force a dry-run report (default when --confirm is absent)" },
            },
            Handler = WithRuntime("purge", (runtime, args) =>
                PurgeAllAsync(runtime, confirm: args.GetFlag("--confirm") && !args.GetFlag("--dry-run"))),
        });

        return parser;
    }

    // ---- shared plumbing ---------------------------------------------------------

    private static List<OptionSpec> ContinuousOptions(params OptionSpec[] extra)
    {
        var options = new List<OptionSpec>
        {
            new() { Name = "--continuous", Help = "Keep running with scheduled full and incremental crawls" },
            new() { Name = "--full-crawl-hours", HasValue = true, MinInt = 12, MaxInt = 168,
                    Help = "Full crawl interval in hours (12-168, default 24)" },
            new() { Name = "--incremental-hours", HasValue = true, MinInt = 1, MaxInt = 168,
                    Help = "Incremental crawl interval in hours (1-168, default 4)" },
        };
        options.AddRange(extra);
        return options;
    }

    /// <summary>Wrap a handler with logging startup, runtime wiring and teardown.</summary>
    private static Func<ParsedArgs, Task<object?>> WithRuntime(
        string logPrefix, Func<Runtime, ParsedArgs, Task<object?>> handler)
    {
        return async args =>
        {
            Logging.Verbose = args.Verbose;
            Logging.StartRun(logPrefix);
            LogPruner.Prune();
            Dashboard.HookCancelKey();
            using var runtime = Runtime.Create();
            // Register the OTLP tracer provider (inert when OTEL_EXPORTER_OTLP_ENDPOINT
            // is unset — nothing registered, zero overhead).
            using var tracing = Telemetry.StartIfConfigured(runtime.Config.ConnectorName);
            using var health = HealthEndpoint.StartIfConfigured(
                runtime.DeadLetterDepth, () => runtime.State.GetBillableLookupCount(),
                runtime.BreakerSnapshots);
            try
            {
                return await handler(runtime, args);
            }
            catch (EntitlementViolationException exc)
            {
                Logger.Error($"ENTITLEMENT: {exc.Message}");
                await runtime.Alerts.SendAsync("critical", "entitlement_violation", exc.Message);
                return false;
            }
            catch (ConfigurationError exc)
            {
                Logger.Error(exc.Message);
                return false;
            }
        };
    }

    internal static GraphSchema LoadGraphSchema(string? path = null)
    {
        path ??= Path.Combine(Directory.GetCurrentDirectory(), "config", "graph-schema.json");
        var schema = JsonSerializer.Deserialize<GraphSchema>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (schema == null || schema.Properties.Count == 0)
            throw new ConfigurationError($"Graph schema at '{path}' is empty or invalid");
        return schema;
    }

    // ---- crawl scheduling / sharding orchestration ------------------------------------

    /// <summary>Setup for every Graph connection this deployment owns: the base
    /// connection, or — under GRAPH_CONNECTION_SHARDS — one per shard.</summary>
    internal static async Task SetupConnectionsAsync(Runtime runtime)
    {
        if (ShardingConfig.TryLoad(out var shards, out var error))
        {
            foreach (var shard in shards)
            {
                Logger.Info($"[shard {shard.ConnectionId}] connection + schema setup " +
                            $"({string.Join(", ", shard.Datasets)})");
                using var shardRuntime = runtime.CreateForShard(shard);
                await shardRuntime.Graph.EnsureConnectionAsync(ServiceStop.Token);
                await shardRuntime.Graph.RegisterSchemaAsync(LoadGraphSchema(), ServiceStop.Token);
            }
            return;
        }
        if (error != null)
            throw new ConfigurationError(error);
        await runtime.Graph.EnsureConnectionAsync(ServiceStop.Token);
        await runtime.Graph.RegisterSchemaAsync(LoadGraphSchema(), ServiceStop.Token);
    }

    /// <summary>
    /// Run one crawl. With GRAPH_CONNECTION_SHARDS set, each shard crawls its own
    /// datasets into its own connection (own state key, dead-letter queue and
    /// seat hash) and the per-shard results are aggregated; a misconfigured
    /// shard map ABORTS the crawl instead of running a partial/overlapping one.
    /// </summary>
    internal static async Task<Ingestion.CrawlResult> RunOneCrawlAsync(
        Runtime runtime, string kind, IReadOnlyCollection<string>? datasets = null)
    {
        if (!ShardingConfig.TryLoad(out var shards, out var error))
        {
            if (error != null)
                throw new ConfigurationError(error);
            var engine = runtime.CreateEngine();
            return await Dashboard.RunAsync(engine,
                () => engine.RunAsync(kind, datasets, ServiceStop.Token));
        }

        var processed = 0;
        var rejected = 0;
        long ingested = 0;
        long deadLettered = 0;
        var stopped = false;
        var closedByThisNode = true;
        string? closeStatus = null;
        var reconciliations = new List<DeliveryReconciliation>();

        foreach (var shard in shards)
        {
            var shardDatasets = datasets == null
                ? shard.Datasets
                : shard.Datasets.Intersect(datasets, StringComparer.OrdinalIgnoreCase).ToList();
            if (shardDatasets.Count == 0)
                continue;
            if (ServiceStop.Requested)
            {
                stopped = true;
                break;
            }

            Logger.Info($"[shard {shard.ConnectionId}] {kind} crawl: {string.Join(", ", shardDatasets)}");
            using var shardRuntime = runtime.CreateForShard(shard);
            var engine = shardRuntime.CreateEngine();
            var result = await engine.RunAsync(kind, shardDatasets, ServiceStop.Token);

            processed += result.DeliveriesProcessed;
            rejected += result.DeliveriesRejected;
            ingested += result.ItemsIngested;
            deadLettered += result.ItemsDeadLettered;
            stopped |= result.Stopped;
            closedByThisNode &= result.ClosedByThisNode;
            if (result.CloseStatus == "failed" || closeStatus == null)
                closeStatus = result.CloseStatus ?? closeStatus;
            reconciliations.AddRange(result.Reconciliations);
        }

        return new Ingestion.CrawlResult(processed, rejected, ingested, deadLettered,
            stopped, reconciliations)
        {
            ClosedByThisNode = closedByThisNode,
            CloseStatus = closeStatus,
        };
    }

    private static async Task SeatSyncOneAsync(Runtime runtime)
    {
        var engine = runtime.CreateEngine();
        var seatSync = runtime.Seats.SyncSeats();
        Console.WriteLine($"Seats: {seatSync.Seats.Count} principal(s), hash {seatSync.SeatHash[..12]}...");
        if (seatSync.RequiresReAcl)
        {
            var updated = await engine.ReAclPassAsync(seatSync, ServiceStop.Token);
            Console.WriteLine($"Seat list changed — re-ACL applied to {updated} item(s).");
        }
        else
        {
            if (seatSync.Changed)
                runtime.Seats.CommitSeatHash(seatSync.SeatHash);
            Console.WriteLine(seatSync.Changed
                ? "First seat sync recorded (no items to re-ACL)."
                : "Seat list unchanged — no re-ACL needed.");
        }
    }

    private static async Task<object?> RunCrawlsAsync(Runtime runtime, ParsedArgs args)
    {
        var continuous = args.GetFlag("--continuous");
        var incremental = args.GetFlag("--incremental");
        var fullEvery = TimeSpan.FromHours(args.GetInt("--full-crawl-hours", 24));
        var incrementalEvery = TimeSpan.FromHours(args.GetInt("--incremental-hours", 4));

        if (!continuous)
        {
            var kind = incremental ? CrawlKind.Incremental : CrawlKind.Full;
            var result = await RunOneCrawlAsync(runtime, kind);
            PrintCrawlResult(result);
            return !result.Stopped && result.DeliveriesRejected == 0;
        }

        Logger.Info($"Continuous mode: full every {fullEvery.TotalHours:0}h, " +
                    $"incremental every {incrementalEvery.TotalHours:0}h");
        var nextFull = DateTime.UtcNow;                       // full crawl immediately
        var nextIncremental = DateTime.UtcNow + incrementalEvery;

        while (!ServiceStop.Requested)
        {
            var now = DateTime.UtcNow;
            if (now >= nextFull)
            {
                PrintCrawlResult(await RunOneCrawlAsync(runtime, CrawlKind.Full));
                nextFull = DateTime.UtcNow + fullEvery;
            }
            else if (now >= nextIncremental)
            {
                PrintCrawlResult(await RunOneCrawlAsync(runtime, CrawlKind.Incremental));
                nextIncremental = DateTime.UtcNow + incrementalEvery;
            }

            var wake = nextFull < nextIncremental ? nextFull : nextIncremental;
            var wait = wake - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30),
                        ServiceStop.Token);
                }
                catch (OperationCanceledException)
                {
                    // Graceful stop requested while sleeping between scheduled
                    // crawls — leave the loop ("Continuous mode stopped" below).
                    break;
                }
            }
        }
        Logger.Info("Continuous mode stopped");
        return true;
    }

    private static void PrintCrawlResult(Ingestion.CrawlResult result)
    {
        var state = result.Degraded ? "PAUSED (degraded — Graph breaker open)"
            : result.Stopped ? "STOPPED (graceful)"
            : "complete";
        Console.WriteLine(
            $"Crawl {state}: " +
            $"{result.DeliveriesProcessed} delivery(ies) reconciled, {result.DeliveriesRejected} rejected, " +
            $"{result.ItemsIngested} item(s) ingested, {result.ItemsDeleted} withdrawn, " +
            $"{result.ItemsSuppressed} suppressed, {result.ItemsDeadLettered} dead-lettered");
    }

    // ---- retry-failed -------------------------------------------------------------------

    /// <summary>Test seam: how shard runtimes are built for shard-aware commands
    /// (retry-failed). Null → the real Runtime.CreateForShard.</summary>
    internal static Func<Runtime, Shard, Runtime>? ShardRuntimeFactory;

    private static Runtime CreateShardRuntime(Runtime baseRuntime, Shard shard) =>
        ShardRuntimeFactory?.Invoke(baseRuntime, shard) ?? baseRuntime.CreateForShard(shard);

    /// <summary>
    /// Replay the dead-letter queue(s). Natively shard-aware: with
    /// GRAPH_CONNECTION_SHARDS set, every shard's queue is replayed against
    /// that shard's own connection (plus the base queue), so no
    /// CONNECTOR_ID=&lt;shardId&gt; workaround is needed.
    /// </summary>
    internal static async Task<object?> RetryFailedAsync(Runtime runtime, bool clearOnSuccess,
        bool retireUnreplayable = false)
    {
        var targets = new List<(Runtime Rt, bool Owned, string Label)>();
        if (ShardingConfig.TryLoad(out var shards, out var shardError))
        {
            foreach (var shard in shards)
                targets.Add((CreateShardRuntime(runtime, shard), true, $"shard {shard.ConnectionId}"));
        }
        else if (shardError != null)
        {
            throw new ConfigurationError(shardError);
        }
        targets.Add((runtime, false, runtime.Config.ConnectorId));

        try
        {
            var totalReplayed = 0;
            var totalRemaining = 0;
            var totalRecords = 0;
            var totalUnreplayable = 0;
            var totalRetired = 0;
            var totalDroppedErased = 0;
            foreach (var (rt, _, label) in targets)
            {
                var result = await RetryFailedCoreAsync(rt, clearOnSuccess, retireUnreplayable, label);
                totalRecords += result.Records;
                totalReplayed += result.Replayed;
                totalRemaining += result.Remaining;
                totalUnreplayable += result.Unreplayable;
                totalRetired += result.Retired;
                totalDroppedErased += result.DroppedErased;
            }

            if (totalRecords == 0)
                Console.WriteLine("Dead-letter queue is empty.");
            else
                Console.WriteLine($"TOTAL: {totalReplayed} replayed, {totalRemaining} still failing" +
                                  (totalRetired > 0 ? $", {totalRetired} retired" : "") +
                                  (totalDroppedErased > 0 ? $", {totalDroppedErased} dropped (erased subject)" : "") +
                                  (totalUnreplayable > 0 ? $", {totalUnreplayable} un-replayable kept" : "") + ".");
            if (totalUnreplayable > 0)
                Console.WriteLine("Un-replayable entries are transform failures (no item payload exists). " +
                                  "Fix the source feed and re-run ingest, or drop them with " +
                                  "retry-failed --retire-unreplayable.");
            // Alert/exit semantics track the REPLAYABLE queue only; un-replayable
            // entries are surfaced above and via altrata_deadletter_unreplayable.
            Metrics.SetDeadLetterDepth(totalRemaining);
            Metrics.Set("altrata_deadletter_unreplayable", totalUnreplayable);
            return totalRemaining == 0;
        }
        finally
        {
            foreach (var (rt, owned, _) in targets)
            {
                if (owned)
                    rt.Dispose();
            }
        }
    }

    private readonly record struct RetryFailedResult(
        int Records, int Replayed, int Remaining, int Unreplayable, int Retired, int DroppedErased);

    /// <summary>Replay one state store's queue against its own Graph connection.
    /// Upsert ops re-PUT the captured item under an ACL REBUILT from the
    /// CURRENT seats (never the ACL captured at dead-letter time — a seat
    /// removed since then must not be re-granted) and record the hash of the
    /// ACL actually sent; REDACTED upserts (DEADLETTER_PAYLOAD_MODE=redacted —
    /// this connector's default) carry no payload and are re-fetched from the
    /// feed delivery (checksum-verified) and re-transformed; delete ops
    /// re-issue the idempotent DELETE (delta tombstones, erasure completions).
    /// DSAR suppression guard: an upsert whose subject was ERASED since it was
    /// queued is DROPPED, never replayed — replaying it would resurrect an
    /// erased subject; delete ops are exempt (they COMPLETE erasures).
    /// Un-replayable entries (transform failures) are kept and reported, or
    /// dropped when <paramref name="retireUnreplayable"/>.</summary>
    private static async Task<RetryFailedResult> RetryFailedCoreAsync(
        Runtime runtime, bool clearOnSuccess, bool retireUnreplayable, string label)
    {
        var records = runtime.State.ReadDeadLetters();
        if (records.Count == 0)
            return new RetryFailedResult(0, 0, 0, 0, 0, 0);

        Console.WriteLine($"[{label}] Replaying {records.Count} dead-lettered record(s)...");
        var remaining = new List<DeadLetterRecord>();
        var unreplayable = new List<DeadLetterRecord>();
        var replayed = 0;
        var retired = 0;
        var droppedErased = 0;
        // Entitlement rebuilt lazily ONCE per queue from the CURRENT seat list.
        // BuildAcl fails closed on an empty seat list, keeping the record queued.
        IReadOnlyList<AclEntry>? currentAcl = null;
        string? currentSeatHash = null;
        // Suppression sets built lazily ONCE per queue: raw ids (full-mode
        // records) and short hashes (redacted records carry hashes only).
        HashSet<string>? suppressedIds = null;
        HashSet<string>? suppressedHashes = null;
        void EnsureSuppressionSets()
        {
            if (suppressedIds != null)
                return;
            var list = runtime.State.ListSuppressedSubjects();
            suppressedIds = new HashSet<string>(list, StringComparer.Ordinal);
            suppressedHashes = list.Select(DeadLetterPolicy.HashSubject)
                .ToHashSet(StringComparer.Ordinal);
        }
        bool ConcernsErasedSubject(DeadLetterRecord r)
        {
            if (r.SubjectIds.Count == 0 && r.SubjectHashes.Count == 0)
                return false;
            EnsureSuppressionSets();
            return r.SubjectIds.Any(suppressedIds!.Contains)
                   || r.SubjectHashes.Any(suppressedHashes!.Contains);
        }
        foreach (var record in records)
        {
            if (!record.IsReplayable)
            {
                if (retireUnreplayable)
                {
                    retired++;
                    Logger.Info($"[{label}] Retired un-replayable dead-letter {record.ItemId} " +
                                $"(op {record.Op}, error: {record.Error})");
                }
                else
                {
                    unreplayable.Add(record);  // kept verbatim — attempts not bumped
                }
                continue;
            }
            if (ServiceStop.Requested)
            {
                remaining.Add(record);
                continue;
            }
            // DSAR suppression guard: never re-PUT an erased subject. The
            // record is DROPPED (suppression wins, exactly like the crawl-time
            // guard); the id-only log line is the audit breadcrumb.
            if (record.Op != DeadLetterOps.Delete && ConcernsErasedSubject(record))
            {
                droppedErased++;
                Logger.Warning($"[{label}] Dead-letter {record.ItemId} concerns an erased " +
                               "(suppressed) subject — dropped, not replayed (suppression wins).");
                continue;
            }
            try
            {
                if (record.Op == DeadLetterOps.Delete)
                {
                    await runtime.Graph.DeleteItemAsync(record.ItemId, ServiceStop.Token);
                    runtime.Identity.RemoveIngestedItem(record.ItemId);
                    replayed++;
                    Metrics.Increment("altrata_items_deleted_total");
                    continue;
                }

                if (currentAcl == null)
                {
                    var seats = runtime.Identity.GetSeats();
                    currentAcl = SeatAclBuilder.BuildAcl(seats);   // throws when seats empty — fail closed
                    currentSeatHash = SeatAclBuilder.ComputeSeatHash(seats);
                }

                ExternalItem item;
                IReadOnlyList<string> subjectsForIndex;
                if (record.Redacted && record.PayloadJson.Length == 0)
                {
                    // Redacted-mode replay: the queue never held the profile —
                    // re-fetch the record from the (checksum-verified) feed
                    // delivery and re-transform under the CURRENT ACL.
                    (item, subjectsForIndex) = RefetchDeadLetterItem(runtime, record, currentAcl);
                    // Re-check suppression against the ACTUAL subject ids from
                    // the feed record (covers records queued without hashes).
                    EnsureSuppressionSets();
                    if (subjectsForIndex.Any(suppressedIds!.Contains))
                    {
                        droppedErased++;
                        Logger.Warning($"[{label}] Dead-letter {record.ItemId} re-fetched to an erased " +
                                       "(suppressed) subject — dropped, not replayed (suppression wins).");
                        continue;
                    }
                }
                else
                {
                    var payload = JsonSerializer.Deserialize<ExternalItem>(record.PayloadJson)
                                  ?? throw new InvalidOperationException("payload unreadable");
                    // Replace the STALE captured ACL with the freshly built one and
                    // record the matching hash, so the re-ACL reconciliation
                    // (ListItemsWithAclHashOtherThan) stays truthful for this item.
                    item = payload with { Acl = currentAcl };
                    subjectsForIndex = record.SubjectIds;
                }
                SeatAclBuilder.AssertNeverEveryone(item.Acl);
                await runtime.Graph.PutItemAsync(item, ServiceStop.Token);
                runtime.Identity.RecordIngestedItem(new Identity.IngestedItem(
                    item.Id, record.Dataset, currentSeatHash!, DateTime.UtcNow));
                // Erasure reverse index: a replayed item must be withdrawable by
                // forget-subject exactly like a first-pass ingest.
                if (subjectsForIndex.Count > 0)
                    runtime.Identity.RecordItemSubjects(item.Id, subjectsForIndex.ToList());
                replayed++;
                Metrics.Increment("altrata_items_ingested_total");
            }
            catch (Exception exc)
            {
                // Kept + re-queued with the attempt bumped: log WHICH record
                // (item id + op + shard label), which attempt this was, and the
                // exception type (payload-unreadable vs Graph rejection differ).
                Logger.Warning($"[{label}] Retry failed for {record.ItemId} " +
                               $"(op {record.Op}, attempt {record.Attempts + 1}): {exc.GetType().Name}: {exc.Message} " +
                               "— record stays on the dead-letter queue.");
                remaining.Add(record with { Attempts = record.Attempts + 1, Error = exc.Message });
            }
        }

        var keep = remaining.Concat(unreplayable).ToList();
        // Atomic finalize (round-3 TOCTOU fix): between the snapshot read at the
        // top and this write, a producer's AddDeadLetter may have appended a new
        // failure (a crawl running alongside retry-failed). A whole-queue
        // overwrite would silently drop it. Instead, remove ONLY the snapshot
        // records we actually processed (matched by a stable content key) and
        // preserve anything that arrived concurrently, then re-queue our kept
        // failures — all inside one lock acquisition.
        var processedBudget = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            var key = DeadLetterIdentityKey(r);
            processedBudget[key] = processedBudget.GetValueOrDefault(key) + 1;
        }
        runtime.State.MutateDeadLetters(current =>
        {
            var budget = new Dictionary<string, int>(processedBudget, StringComparer.Ordinal);
            var survivors = new List<DeadLetterRecord>(current.Count + keep.Count);
            foreach (var rec in current)
            {
                var key = DeadLetterIdentityKey(rec);
                if (budget.TryGetValue(key, out var n) && n > 0)
                {
                    budget[key] = n - 1;   // one of the snapshot we consumed — drop it
                    continue;
                }
                survivors.Add(rec);        // arrived concurrently — never lose it
            }
            survivors.AddRange(keep);
            return survivors;
        });
        if (keep.Count == 0 && clearOnSuccess)
            Console.WriteLine($"[{label}] All {replayed} record(s) replayed; dead-letter queue cleared.");
        else
            Console.WriteLine($"[{label}] {replayed} replayed, {remaining.Count} still failing" +
                              (retired > 0 ? $", {retired} retired" : "") +
                              (droppedErased > 0 ? $", {droppedErased} dropped (erased subject)" : "") +
                              (unreplayable.Count > 0 ? $", {unreplayable.Count} un-replayable kept" : "") + ".");
        return new RetryFailedResult(records.Count, replayed, remaining.Count, unreplayable.Count,
            retired, droppedErased);
    }

    /// <summary>Stable content key for a dead-letter record: distinguishes a
    /// record we read in our snapshot from one a producer appended concurrently,
    /// so retry-failed's atomic finalize removes only what it processed.</summary>
    private static string DeadLetterIdentityKey(DeadLetterRecord r) => string.Join('\u001f',
        r.ItemId, r.Op, r.Dataset, r.DeliveryId, r.Error,
        r.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
        r.FailedUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        r.CorrelationId ?? "",
        r.PayloadJson.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string.Join(",", r.SubjectHashes));

    /// <summary>
    /// Redacted-mode replay source: locate the record's delivery under
    /// FEED_PATH, read its dataset file(s) with the manifest checksum verified
    /// on the same open handle, find the record whose item id matches, and
    /// re-transform it under the given (CURRENT) ACL. Throws with an
    /// actionable message when the delivery/record is gone — the record then
    /// stays queued (fix: re-deliver the feed drop, or re-ingest from source).
    /// NOTE the retention interplay: RETENTION_MODE=delete can remove the
    /// delivery before the queue drains (docs/RUNBOOKS.md "Dead-letter growth").
    /// </summary>
    internal static (ExternalItem Item, IReadOnlyList<string> SubjectIds) RefetchDeadLetterItem(
        Runtime runtime, DeadLetterRecord record, IReadOnlyList<AclEntry> acl)
    {
        var feedPath = runtime.Config.FeedPath
            ?? throw new InvalidOperationException(
                "FEED_PATH is not set — cannot re-fetch a redacted dead-letter payload from the feed");
        var delivery = FeedReader.DiscoverDeliveries(feedPath)
                           .FirstOrDefault(d => string.Equals(d.Id, record.DeliveryId, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException(
                           $"delivery '{record.DeliveryId}' is no longer under FEED_PATH " +
                           "(archived/removed by retention?) — restore the delivery or re-ingest from source");

        var transformer = BuildRetryTransformer(runtime);
        foreach (var file in delivery.Manifest.Files)
        {
            if (!string.Equals(Datasets.Canonical(file.Dataset), record.Dataset, StringComparison.Ordinal))
                continue;
            var records = FeedReader.ReadRecords(
                Path.Combine(delivery.Directory, file.Name), record.Dataset, file.Sha256);
            foreach (var feedRecord in records)
            {
                if (feedRecord.IsTombstone || feedRecord.Id == null)
                    continue;
                if (!string.Equals(ItemTransformer.BuildItemId(record.Dataset, feedRecord.Id),
                        record.ItemId, StringComparison.Ordinal))
                    continue;
                return (transformer.Transform(feedRecord, acl), ItemTransformer.SubjectIds(feedRecord));
            }
        }
        throw new InvalidOperationException(
            $"record for item '{record.ItemId}' not found in delivery '{record.DeliveryId}' " +
            $"({record.Dataset}) — the delivery content changed; re-ingest from source");
    }

    /// <summary>Transformer for redacted-mode replays: entity resolution +
    /// classification exactly like the crawl; the relationship-path join is
    /// intentionally skipped (rebuilding the index for one item is unbounded
    /// work — the next full crawl re-materializes path properties idempotently).</summary>
    private static ItemTransformer BuildRetryTransformer(Runtime runtime)
    {
        var fuzzy = new Identity.FuzzyOptions
        {
            Enabled = runtime.Config.EntityFuzzyMatching,
            Threshold = runtime.Config.EntityMatchThreshold,
            ReviewFloor = runtime.Config.EntityReviewFloor,
            Review = new Identity.MatchReviewQueue(runtime.Config.ConnectorId),
        };
        return new ItemTransformer(
            new Identity.EntityResolver(runtime.Identity, fuzzy),
            classification: new ClassificationOptions
            {
                Enabled = runtime.Config.Classification,
                Residency = runtime.Config.DataResidency,
            });
    }

    // ---- identity-dry-run -------------------------------------------------------------------

    internal static Task<object?> IdentityDryRunAsync(Runtime runtime, bool save)
    {
        var seats = runtime.Seats.LoadSeats();
        var hash = SeatAclBuilder.ComputeSeatHash(seats);
        var storedHash = runtime.State.GetValue(StateKeys.SeatListHash);

        Console.WriteLine("Identity dry run");
        Console.WriteLine("----------------");
        Console.WriteLine($"Seat source        : " +
                          (runtime.Config.SeatGroupId != null
                              ? $"SEAT_GROUP_ID={runtime.Config.SeatGroupId}"
                              : runtime.Config.SeatListPath));
        Console.WriteLine($"Seat principals    : {seats.Count}");
        foreach (var group in seats.GroupBy(s => s.Kind))
            Console.WriteLine($"  {group.Key,-14}: {group.Count()}");
        Console.WriteLine($"Seat hash          : {hash}");
        Console.WriteLine($"Stored hash        : {storedHash ?? "(none)"}");
        Console.WriteLine($"Re-ACL needed      : {(storedHash != null && storedHash != hash ? "YES" : "no")}");

        if (!string.IsNullOrEmpty(runtime.Config.CrmContactsPath) && File.Exists(runtime.Config.CrmContactsPath))
        {
            var contacts = Ingestion.CrawlEngine.LoadCrmContactFile(runtime.Config.CrmContactsPath);
            Console.WriteLine($"CRM contacts file  : {runtime.Config.CrmContactsPath} ({contacts.Count} contact(s))");
            if (save)
                runtime.Identity.ReplaceCrmContacts(contacts);
        }
        else
        {
            Console.WriteLine($"CRM contacts store : {runtime.Identity.CountCrmContacts()} contact(s)");
        }
        Console.WriteLine($"Crosswalk links    : {runtime.Identity.ListCrosswalk().Count}");
        Console.WriteLine($"Ingested items     : {runtime.Identity.CountIngestedItems()}");

        if (save)
        {
            runtime.Identity.ReplaceSeats(seats);
            Console.WriteLine("Saved seats (and contacts if present) to the identity store. " +
                              "Seat hash NOT committed — run seat-sync to apply ACL changes.");
        }
        return Task.FromResult<object?>(true);
    }

    // ---- purge-all ------------------------------------------------------------------------------

    internal static async Task<object?> PurgeAllAsync(Runtime runtime, bool confirm)
    {
        // Under connection sharding each shard connection owns part of the data;
        // the license-end purge must cover every shard PLUS the base state.
        var targets = new List<(Runtime Rt, bool Owned, string Label)>();
        if (ShardingConfig.TryLoad(out var shards, out var shardError))
        {
            foreach (var shard in shards)
                targets.Add((runtime.CreateForShard(shard), true, $"shard {shard.ConnectionId}"));
        }
        else if (shardError != null)
        {
            throw new ConfigurationError(shardError);
        }
        targets.Add((runtime, false, runtime.Config.ConnectorId));

        try
        {
            Console.WriteLine("Purge scope");
            Console.WriteLine("-----------");
            var totalItems = 0;
            foreach (var (rt, _, label) in targets)
            {
                var items = rt.Identity.ListIngestedItems();
                totalItems += items.Count;
                Console.WriteLine($"[{label}]");
                Console.WriteLine($"  External items to withdraw : {items.Count}");
                Console.WriteLine($"  Dead-letter records        : {rt.State.ReadDeadLetters().Count}");
                Console.WriteLine($"  Crosswalk links            : {rt.Identity.ListCrosswalk().Count}");
                Console.WriteLine($"  Processed-delivery ledger  : {rt.State.ListProcessedDeliveries().Count}");
                Console.WriteLine($"  Billable lookups (counter) : {rt.State.GetBillableLookupCount()}");
            }
            Console.WriteLine("Note: the audit log is retained (it documents lawful use).");

            if (!confirm)
            {
                Console.WriteLine();
                Console.WriteLine("DRY RUN — nothing was deleted. Re-run with --confirm to purge.");
                return true;
            }

            Console.WriteLine();
            Console.WriteLine("Purging...");
            var withdrawn = 0;
            var failures = 0;
            foreach (var (rt, _, label) in targets)
            {
                var rtFailures = 0;
                foreach (var item in rt.Identity.ListIngestedItems())
                {
                    try
                    {
                        await rt.Graph.DeleteItemAsync(item.ItemId, ServiceStop.Token);
                        rt.Identity.RemoveIngestedItem(item.ItemId);
                        withdrawn++;
                        Metrics.Increment("altrata_items_deleted_total");
                    }
                    catch (Exception exc)
                    {
                        // Counted: any failure keeps this target's state UNWIPED
                        // (see below) so the purge can be re-run to completion.
                        rtFailures++;
                        Logger.Error($"[{label}] Failed to withdraw {item.ItemId}: " +
                                     $"{exc.GetType().Name}: {exc.Message} — state NOT wiped; re-run purge-all --confirm.");
                    }
                }

                if (rtFailures == 0)
                {
                    rt.State.WipeAll();
                    rt.Identity.WipeAll();
                }
                failures += rtFailures;
            }

            if (failures > 0)
            {
                Console.WriteLine($"{withdrawn} withdrawn, {failures} FAILED — affected state NOT wiped; " +
                                  "fix connectivity and re-run purge-all --confirm.");
                return false;
            }

            runtime.Audit.Append(new AuditEntry
            {
                Actor = Environment.UserName,
                Action = "purge_all",
                Purpose = "license-end purge",
                Billable = false,
            });
            Console.WriteLine($"{withdrawn} item(s) withdrawn; local state wiped. License-end purge complete.");
            return true;
        }
        finally
        {
            foreach (var (rt, owned, _) in targets)
            {
                if (owned)
                    rt.Dispose();
            }
        }
    }

    // ---- forget-subject (per-subject erasure / DSAR) -----------------------------------------------

    /// <summary>
    /// Erase one person: resolve them (by Altrata id, or by email via the
    /// crosswalk), withdraw every externalItem concerning them (PersonProfile +
    /// derived, via the item↔subject reverse index), remove them from the
    /// ingested inventory, crosswalk and relationship-path index, add them to
    /// the durable suppression list (so re-delivery never re-ingests them), and
    /// append a tamper-evident erasure-ledger entry. Dry-run (default without
    /// --confirm) reports counts + item ids without mutating anything.
    /// </summary>
    internal static async Task<object?> ForgetSubjectAsync(
        Runtime runtime, string? altrataId, string? email, string actor, bool confirm)
    {
        if (string.IsNullOrWhiteSpace(altrataId) && string.IsNullOrWhiteSpace(email))
        {
            Logger.Error("forget-subject requires --id or --email");
            return false;
        }

        // One correlation id + span for the erasure cycle. PII CAUTION: the
        // span tags the resolution MODE (id/email) and counts only — never the
        // email address or any profile field.
        using var correlation = CorrelationContext.Begin();
        using var span = Telemetry.Span("forget-subject");
        Telemetry.SetTag(span, "altrata.subject.resolved_by",
            !string.IsNullOrWhiteSpace(altrataId) ? "id" : "email");

        // Build the target set: every shard connection's store PLUS the base
        // state. Under GRAPH_CONNECTION_SHARDS the ingested items, crosswalk,
        // reverse index and suppression list live in each shard's own store, so
        // an erasure that touched only the base would withdraw nothing, suppress
        // nothing, and the subject would be re-ingested on the next shard crawl.
        // This mirrors retry-failed / purge-all.
        var targets = new List<(Runtime Rt, bool Owned, string Label)>();
        if (ShardingConfig.TryLoad(out var shards, out var shardError))
        {
            foreach (var shard in shards)
                targets.Add((CreateShardRuntime(runtime, shard), true, $"shard {shard.ConnectionId}"));
        }
        else if (shardError != null)
        {
            throw new ConfigurationError(shardError);
        }
        targets.Add((runtime, false, runtime.Config.ConnectorId));

        try
        {
            // Resolve subject id(s) across EVERY target: direct id, or every
            // altrata id linked to the email's CRM contact through any target's
            // crosswalk (an email may only resolve in the shard that ingested it).
            var subjectIds = new SortedSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(altrataId))
                subjectIds.Add(altrataId.Trim());
            if (!string.IsNullOrWhiteSpace(email))
            {
                var normalized = email.Trim().ToLowerInvariant();
                var matched = false;
                foreach (var (rt, _, _) in targets)
                {
                    var contact = rt.Identity.FindContactByEmail(normalized);
                    if (contact == null)
                        continue;
                    matched = true;
                    foreach (var entry in rt.Identity.ListCrosswalk())
                    {
                        if (string.Equals(entry.CrmContactId, contact.Id, StringComparison.Ordinal))
                            subjectIds.Add(entry.AltrataId);
                    }
                }
                if (!matched)
                    Console.WriteLine($"No CRM contact matches email '{email}'. Nothing to erase by email.");
            }

            if (subjectIds.Count == 0)
            {
                Console.WriteLine("Could not resolve any subject to erase.");
                return false;
            }

            // Per-target withdraw set: items concerning the subject (reverse
            // index + the PersonProfile id) that actually exist in that target's
            // inventory.
            var perTarget = new List<(Runtime Rt, string Label, List<string> Withdraw)>();
            var allWithdraw = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (rt, _, label) in targets)
            {
                var itemIds = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var id in subjectIds)
                {
                    foreach (var itemId in rt.Identity.ListItemsForSubject(id))
                        itemIds.Add(itemId);
                    itemIds.Add(ItemTransformer.BuildItemId(Datasets.PersonProfile, id));
                }
                var inventory = rt.Identity.ListIngestedItems().Select(i => i.ItemId)
                    .ToHashSet(StringComparer.Ordinal);
                var toWithdraw = itemIds.Where(inventory.Contains).ToList();
                perTarget.Add((rt, label, toWithdraw));
                foreach (var w in toWithdraw)
                    allWithdraw.Add(w);
            }

            Telemetry.SetTag(span, "altrata.subject.count", subjectIds.Count);
            Telemetry.SetTag(span, "altrata.items.count", allWithdraw.Count);

            Console.WriteLine("Erasure scope");
            Console.WriteLine("-------------");
            Console.WriteLine($"Subject id(s)          : {string.Join(", ", subjectIds)}");
            if (!string.IsNullOrWhiteSpace(email))
                Console.WriteLine($"Resolved from email    : {email}");
            Console.WriteLine($"Items to withdraw      : {allWithdraw.Count}");
            foreach (var (_, label, withdraw) in perTarget.Where(t => t.Withdraw.Count > 0))
            {
                Console.WriteLine($"  [{label}] {withdraw.Count} item(s)");
                foreach (var itemId in withdraw)
                    Console.WriteLine($"    - {itemId}");
            }
            var alreadySuppressed = subjectIds.Count(id => targets.All(t => t.Rt.State.IsSubjectSuppressed(id)));
            Console.WriteLine($"Already suppressed     : {alreadySuppressed} / {subjectIds.Count}");

            if (!confirm)
            {
                Console.WriteLine();
                Console.WriteLine("DRY RUN — nothing was erased. Re-run with --confirm to execute.");
                return true;
            }

            Console.WriteLine();
            Console.WriteLine("Erasing...");

            // Suppress FIRST — before any withdrawal (round-2 DSAR-race fix).
            // A crawl racing this erasure re-checks the suppression list AFTER
            // its $batch PUT lands and withdraws any item whose subject is
            // suppressed by then (CrawlEngine.IngestRecordsAsync). Ordering the
            // suppression ahead of the withdrawals makes that guard airtight:
            // any in-flight batch this withdrawal loop cannot see (its items are
            // not inventoried yet) is guaranteed to observe the suppression at
            // its post-PUT re-check. Fail-closed too — if this process dies
            // mid-erasure the subject stays suppressed; unsuppress-subject is
            // the explicit way back.
            foreach (var id in subjectIds)
            {
                foreach (var (rt, _, _) in perTarget)
                    rt.State.AddSuppressedSubject(id);
            }

            // Dead-letter scrub: queued UPSERT/TRANSFORM records for the erased
            // subject are dropped from every target's queue. In FULL payload
            // mode those records hold the subject's whole profile AT REST — an
            // erasure that left them behind would be incomplete; in redacted
            // mode this only prunes noise. Queued DELETE records are KEPT (they
            // COMPLETE erasures/tombstones). retry-failed has a matching guard
            // for records this scrub cannot identify (queued pre-1.0, before
            // subject stamping) once they are re-fetched.
            var erasedHashes = subjectIds.Select(State.DeadLetterPolicy.HashSubject)
                .ToHashSet(StringComparer.Ordinal);
            var scrubbed = 0;
            foreach (var (rt, label, _) in perTarget)
            {
                var scrubbedHere = 0;
                // Atomic read-modify-write (round-3 TOCTOU fix): a crawl producing
                // dead-letters concurrently — most dangerously a compensating
                // erasure DELETE — must not be lost to a stale-snapshot overwrite.
                // The transform runs under the queue lock over the CURRENT records.
                rt.State.MutateDeadLetters(current =>
                {
                    var keep = new List<DeadLetterRecord>(current.Count);
                    scrubbedHere = 0;
                    foreach (var queued in current)
                    {
                        var concernsSubject = queued.SubjectHashes.Any(erasedHashes.Contains)
                                              || queued.SubjectIds.Any(subjectIds.Contains);
                        if (concernsSubject && queued.Op != DeadLetterOps.Delete)
                        {
                            scrubbedHere++;
                            continue;
                        }
                        keep.Add(queued);
                    }
                    return keep;
                });
                if (scrubbedHere > 0)
                {
                    scrubbed += scrubbedHere;
                    Logger.Warning($"[{label}] Erasure scrubbed {scrubbedHere} queued dead-letter " +
                                   "record(s) concerning the erased subject(s) — queued payloads for an " +
                                   "erased subject must not stay at rest or be replayed.");
                }
            }
            if (scrubbed > 0)
                Console.WriteLine($"Scrubbed {scrubbed} queued dead-letter record(s) for the subject(s).");

            var withdrawn = 0;
            var failures = 0;
            foreach (var (rt, label, toWithdraw) in perTarget)
            {
                foreach (var itemId in toWithdraw)
                {
                    try
                    {
                        await rt.Graph.DeleteItemAsync(itemId, ServiceStop.Token);
                        rt.Identity.RemoveIngestedItem(itemId);
                        withdrawn++;
                        Metrics.Increment("altrata_items_erased_total");
                        Metrics.Increment("altrata_items_deleted_total");
                    }
                    catch (Exception exc)
                    {
                        // Durability: still suppress + ledger, but queue the DELETE
                        // (on this target's queue) for retry-failed to complete.
                        failures++;
                        Logger.Error($"[{label}] Erasure withdrawal failed for {itemId}: " +
                                     $"{exc.GetType().Name}: {exc.Message} — DELETE dead-lettered; " +
                                     "suppression stays durable, run retry-failed to complete the erasure.");
                        rt.State.AddDeadLetter(new DeadLetterRecord
                        {
                            ItemId = itemId,
                            Dataset = "",
                            DeliveryId = "erasure",
                            Error = exc.Message,
                            Op = DeadLetterOps.Delete,
                            PayloadJson = "",
                            CorrelationId = CorrelationContext.Current,
                            // Hashes only — the compensating DELETE must stay
                            // PII-safe AND survive the scrub above (delete ops
                            // are exempt: they COMPLETE the erasure).
                            SubjectHashes = erasedHashes.ToList(),
                        });
                        rt.Identity.RemoveIngestedItem(itemId);
                    }
                }
            }

            // Local erasure on EVERY target's store (suppression already durable
            // above, ahead of the withdrawals); ledger once (base) per subject.
            foreach (var id in subjectIds)
            {
                foreach (var (rt, _, _) in perTarget)
                {
                    rt.Identity.RemoveCrosswalk(id);
                    rt.Identity.RemovePersonFromPathIndex(id);
                }
                runtime.Erasure.Append(actor, ErasureActions.Erase, id, email, allWithdraw.ToList());
                Metrics.Increment("altrata_subjects_erased_total");
            }

            Metrics.Set("altrata_suppression_list_size", runtime.State.ListSuppressedSubjects().Count);
            // Post-append chain verification: sets the altrata_erasure_ledger_broken
            // gauge (SIEM tamper alert) and catches a torn write immediately.
            runtime.Erasure.Verify(out _);
            Telemetry.SetTag(span, "altrata.items.withdrawn", withdrawn);
            Telemetry.SetTag(span, "altrata.items.failed", failures);

            if (failures > 0)
            {
                Console.WriteLine($"{withdrawn} item(s) withdrawn, {failures} queued for retry (dead-lettered DELETE). " +
                                  $"{subjectIds.Count} subject(s) suppressed and ledgered. " +
                                  "Run retry-failed to complete the Graph withdrawals.");
                return false;
            }
            Console.WriteLine($"{withdrawn} item(s) withdrawn; {subjectIds.Count} subject(s) suppressed and ledgered. " +
                              "Erasure complete and durable against re-delivery.");
            return true;
        }
        finally
        {
            foreach (var (rt, owned, _) in targets)
                if (owned)
                    rt.Dispose();
        }
    }

    internal static Task<object?> UnsuppressSubjectAsync(
        Runtime runtime, string altrataId, string actor, bool confirm)
    {
        var id = altrataId.Trim();

        // Shard-aware: the suppression lives in each shard's own state (plus the
        // base), so lifting a block must clear every store — otherwise a shard
        // keeps skipping the subject.
        var targets = new List<(Runtime Rt, bool Owned)>();
        if (ShardingConfig.TryLoad(out var shards, out var shardError))
        {
            foreach (var shard in shards)
                targets.Add((CreateShardRuntime(runtime, shard), true));
        }
        else if (shardError != null)
        {
            throw new ConfigurationError(shardError);
        }
        targets.Add((runtime, false));

        try
        {
            if (targets.All(t => !t.Rt.State.IsSubjectSuppressed(id)))
            {
                Console.WriteLine($"Subject '{id}' is not suppressed — nothing to do.");
                return Task.FromResult<object?>(true);
            }
            if (!confirm)
            {
                Console.WriteLine($"DRY RUN — subject '{id}' WOULD be un-suppressed (re-ingestable on the next crawl). " +
                                  "Re-run with --confirm.");
                return Task.FromResult<object?>(true);
            }
            foreach (var (rt, _) in targets)
            {
                if (rt.State.IsSubjectSuppressed(id))
                    rt.State.RemoveSuppressedSubject(id);
            }
            runtime.Erasure.Append(actor, ErasureActions.Unsuppress, id, null, Array.Empty<string>());
            runtime.Erasure.Verify(out _);   // refresh the ledger-integrity gauge
            Metrics.Set("altrata_suppression_list_size", runtime.State.ListSuppressedSubjects().Count);
            Console.WriteLine($"Subject '{id}' un-suppressed; it may be ingested again on the next crawl.");
            return Task.FromResult<object?>(true);
        }
        finally
        {
            foreach (var (rt, owned) in targets)
                if (owned)
                    rt.Dispose();
        }
    }
}
