// Webhook/WebhookProcessor.cs
// ---------------------------
// Drains the debouncer and applies each coalesced event as targeted
// incremental work, reusing the polling pipeline's single-item paths:
//
//   Upsert → RetrieveAsync(id) + IngestPipeline.IngestSingleAsync — re-ingest
//            the current record (records the inventory, shard-routes). A record
//            that no longer exists in Clarizen is withdrawn instead (a delete
//            that raced ahead of its notification).
//   Delete → IngestPipeline.WithdrawSingleAsync — Graph DELETE + inventory
//            drop (reuses the round-1 deletion/inventory machinery).
//
// Unknown object types (not in config/schema.json) are dropped with a warning.
// Every event is applied independently; one failure never stalls the queue —
// failures are logged and the crawl's polling cursor remains the backstop.
//
// The worker loop is clock- and delay-injectable so tests drive it
// deterministically without real time.

using ClarizenConnector.Clarizen;
using ClarizenConnector.Config;
using ClarizenConnector.Graph;
using ClarizenConnector.Infrastructure;

namespace ClarizenConnector.Webhook;

public sealed class WebhookProcessor
{
    private static readonly IAppLogger Logger = Logging.GetLogger("clarizen_connector.webhook");

    private readonly SchemaConfig _schema;
    private readonly ClarizenClient _clarizen;
    private readonly IngestPipeline _pipeline;
    private readonly EventDebouncer _debouncer;

    /// <summary>Clock seam (UTC now); tests inject a virtual clock.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Delay seam so the worker loop never really sleeps in tests.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        (delay, ct) => Task.Delay(delay, ct);

    public WebhookProcessor(
        SchemaConfig schema,
        ClarizenClient clarizen,
        IngestPipeline pipeline,
        TimeSpan? debounceWindow = null)
    {
        _schema = schema;
        _clarizen = clarizen;
        _pipeline = pipeline;
        _debouncer = new EventDebouncer(debounceWindow
            ?? TimeSpan.FromMilliseconds(EnvFlags.GetInt("CLARIZEN_WEBHOOK_DEBOUNCE_MS", 2000)));
    }

    public EventDebouncer Debouncer => _debouncer;

    /// <summary>Enqueue a validated event (called by the receiver). Coalesced per entity.</summary>
    public void Enqueue(WebhookEvent evt) => _debouncer.Offer(evt, UtcNow());

    /// <summary>
    /// Run the drain loop until <paramref name="stop"/> is cancelled, then
    /// flush any pending events so nothing validated is lost on shutdown.
    /// </summary>
    public async Task RunAsync(CancellationToken stop)
    {
        var poll = TimeSpan.FromMilliseconds(
            Math.Max(50, EnvFlags.GetInt("CLARIZEN_WEBHOOK_POLL_MS", 250)));
        Logger.Info("Webhook processor started.");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await DrainOnceAsync(stop).ConfigureAwait(false);
                try
                {
                    await DelayAsync(poll, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            // Final flush: apply everything still pending (ignore the window).
            foreach (var evt in _debouncer.DrainAll())
                await ApplyAsync(evt, CancellationToken.None).ConfigureAwait(false);
            Logger.Info("Webhook processor stopped.");
        }
    }

    /// <summary>Drain the entities whose window elapsed and apply each. Testable.</summary>
    public async Task DrainOnceAsync(CancellationToken ct = default)
    {
        foreach (var evt in _debouncer.DrainReady(UtcNow()))
        {
            if (ct.IsCancellationRequested)
                break;
            await ApplyAsync(evt, ct).ConfigureAwait(false);
        }
    }

    internal async Task ApplyAsync(WebhookEvent evt, CancellationToken ct)
    {
        // Each webhook event is its own traceable unit of work: a fresh
        // correlation id + root span so logs/dead-letter for this event are
        // followable independently of any crawl cycle.
        using var eventSpan = Tracing.StartWebhookEvent(evt.ObjectType, evt.Kind.ToString());
        using var correlation = CorrelationContext.Begin();
        eventSpan?.SetTag("clarizen.correlation_id", CorrelationContext.Current);
        eventSpan?.SetTag("clarizen.item_id", evt.ItemId);

        var objectConfig = _schema.FindObject(evt.ObjectType);
        if (objectConfig is null)
        {
            Logger.Warning(
                $"Webhook: dropping event for unknown object type '{evt.ObjectType}' "
                + "(not in config/schema.json).");
            return;
        }

        try
        {
            if (evt.Kind == ChangeKind.Delete)
            {
                var (ok, error) = await _pipeline
                    .WithdrawSingleAsync(objectConfig, evt.LocalId, ct).ConfigureAwait(false);
                Logger.Info(ok
                    ? $"Webhook: withdrew {evt.ItemId}."
                    : $"Webhook: failed to withdraw {evt.ItemId}: {error}");
                return;
            }

            var row = await _clarizen.RetrieveAsync(objectConfig, evt.LocalId, ct).ConfigureAwait(false);
            if (row is null)
            {
                // The record is gone from the source — treat the upsert as a
                // delete (a removal that outran its own notification).
                var (ok, error) = await _pipeline
                    .WithdrawSingleAsync(objectConfig, evt.LocalId, ct).ConfigureAwait(false);
                Logger.Info(ok
                    ? $"Webhook: {evt.ItemId} no longer in Clarizen — withdrew it."
                    : $"Webhook: {evt.ItemId} missing and withdraw failed: {error}");
                return;
            }

            var record = new ClarizenRecord(objectConfig.ObjectName, row);
            var (ingestOk, ingestError) = await _pipeline
                .IngestSingleAsync(record, objectConfig, ct).ConfigureAwait(false);
            Logger.Info(ingestOk
                ? $"Webhook: re-ingested {evt.ItemId}."
                : $"Webhook: failed to ingest {evt.ItemId}: {ingestError}");
        }
        catch (Exception exc)
        {
            // One event's failure must never stall the queue; the polling
            // cursor is the backstop that eventually reconciles it.
            Logger.Warning($"Webhook: error applying event for {evt.ItemId}: {exc.Message}");
        }
    }
}
