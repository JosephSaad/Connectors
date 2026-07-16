# Distributed tracing & correlation IDs

The connector emits OpenTelemetry spans around the meaningful pipeline stages
and stamps a **correlation id** on every log line, dead-letter record, and span
for a run — so a single crawl (or webhook event) is followable end-to-end.

> **Off by default, zero overhead.** With `OTEL_EXPORTER_OTLP_ENDPOINT` unset,
> no `TracerProvider` and no `ActivityListener` are registered. Every span helper
> then returns `null` in O(1) (an internal `ActivitySource.HasListeners()`
> short-circuit), so the instrumentation is a genuine no-op — default behaviour
> and overhead are unchanged. The correlation id is still stamped on logs and
> dead-letter records (that path is independent of the exporter).

## Spans

One `ActivitySource` named **`ClarizenConnector`**. Span tree per crawl cycle:

```
crawl.cycle                       (kind, connector.id, correlation_id)
└─ crawl.object                   (object_type, connector.id, correlation_id)
   ├─ source.fetch                (object_type, source = rest|tdw)
   ├─ transform.chunk             (object_type, record_count, item_count)
   ├─ graph.ingest                (object_type, item_count)
   └─ deletion.sweep              (object_type, stale_count)   [full crawls]

webhook.event                     (object_type, change_kind, item_id, correlation_id)
└─ (targeted upsert/withdraw reuses the single-item pipeline paths)
```

Spans are parented by `Activity.Current`, so the tree reflects the call graph.
Every span carries the ambient `clarizen.correlation_id` tag.

## Correlation id

- One id per **crawl cycle** (a `RunAsync` call) and one per **webhook event**.
- 32-hex, W3C trace-id shaped. When a trace span is active for the cycle the id
  **is** the span's `TraceId`, so logs, dead-letter records, and traces share a
  single identifier; when tracing is off it's a fresh random id.
- Flows across `async`/`await` and `Task.Run` via `AsyncLocal`, so concurrent
  object workers within a cycle all log the same id.
- Appears as `correlation_id` on structured JSON logs (`LOG_FORMAT=json`), as an
  `[8-char]` prefix on text logs, on dead-letter records (JSONL and the SQL
  `dbo.DeadLetter.CorrelationId` column), and as a span tag.

Follow one crawl end-to-end:

```bash
grep '"correlation_id":"<id>"' logs/*/connector.log        # every log line
grep '"correlation_id":"<id>"' logs/failed_records_*.jsonl  # its failures
# and query traces by trace id <id> in your OTLP backend
```

## OTLP export

Gated on `OTEL_EXPORTER_OTLP_ENDPOINT`. The standard OTEL environment variables
are honoured:

| Var | Effect |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Collector endpoint. **Set → tracing on.** Unset → off (no-op). |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (default) or `http/protobuf`. |
| `OTEL_EXPORTER_OTLP_HEADERS` | Extra export headers (e.g. auth). |
| `OTEL_SERVICE_NAME` | `service.name` resource attribute; defaults to the connector name (`CONNECTOR_NAME`). |

The exporter uses a **batch** processor (background drain, bounded queue,
drop-on-overflow) with a bounded flush on shutdown, so a slow or unreachable
collector can never fail or stall a crawl — export is fire-and-forget. A broken
exporter config is logged and falls back to off; it never throws into a command.

## Observability

- `validate-config` prints the exporter target (or "disabled") — informational,
  never a failure.
- `/metrics` includes `clarizen_connector_tracing_enabled` (gauge: 1 when the
  exporter is registered, else 0).
