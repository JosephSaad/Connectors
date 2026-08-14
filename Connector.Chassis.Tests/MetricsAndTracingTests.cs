// MetricsAndTracingTests.cs
// -------------------------
// MetricsRenderer + Tracing: the two chassis modules whose output is consumed
// by machines rather than people, so a silent format regression is invisible
// until a Prometheus scrape rejects the page or a trace stops correlating.
//
// What is deliberately pinned here:
//   * the exact three-line Prometheus family shape, LF-terminated (an
//     AppendLine "tidy-up" would emit CRLF on Windows and nothing else in the
//     fleet would notice);
//   * numeric rendering under a NON-invariant CurrentCulture — every sample
//     value must still use '.' and '-'. This is the single most common way a
//     .NET metrics endpoint breaks: it passes on the developer's en-US box and
//     emits "1,5" on a de-DE server, which Prometheus rejects;
//   * ordinal (not culture) label ordering, and that the escape switch is
//     honoured in both positions;
//   * an empty registry rendering a valid, sample-free page instead of throwing;
//   * that BeginCycle actually EMITS a span on the shared ActivitySource
//     (captured with a real ActivityListener — a grep cannot tell a span from a
//     using-directive), while still handing back a usable CorrelationId when
//     export is off. Export is opt-in; correlation is not.
//
// Every global these touch (Chassis.Identity, CircuitBreakerRegistry,
// Tracing.ServiceName / correlation slot, OTEL_* env vars, CurrentCulture,
// Activity.Current) is snapshotted and restored, because this assembly is
// shared with every other chassis suite.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Connector.Chassis.Tests;

// ── scopes: every global mutated below is restored on Dispose ────────────────

/// <summary>Installs a known <see cref="ChassisIdentity"/> and restores the previous one.</summary>
internal sealed class MetricsTracingIdentityScope : IDisposable
{
    private readonly ChassisIdentity _previous;

    public MetricsTracingIdentityScope(string connectorId)
    {
        // Touch Tracing BEFORE swapping the identity. Tracing's single
        // ActivitySource is constructed in its type initializer from
        // Chassis.Identity.EventLogSource, so whichever test first loads the
        // type freezes that name for the whole assembly. Forcing the load here,
        // under the AMBIENT identity, stops these tests from freezing it to a
        // throwaway test name and breaking source-name filtering for everyone
        // else in the merged run.
        _ = Tracing.ActivitySourceName;

        _previous = Chassis.Identity;
        Chassis.Init(new ChassisIdentity(connectorId, $"{connectorId}.EventSource", connectorId));
    }

    public void Dispose() => Chassis.Init(_previous);
}

/// <summary>
/// Makes CurrentCulture a culture whose decimal separator is ',' and whose
/// negative sign is '!'. Built by cloning the invariant culture rather than
/// naming a real one (de-DE) so the test asserts the same thing under
/// globalization-invariant mode and across ICU versions, on Linux and Windows.
/// </summary>
internal sealed class MetricsTracingCultureScope : IDisposable
{
    private readonly CultureInfo _previousCulture;
    private readonly CultureInfo _previousUiCulture;

    public MetricsTracingCultureScope()
    {
        _previousCulture = CultureInfo.CurrentCulture;
        _previousUiCulture = CultureInfo.CurrentUICulture;

        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NumberDecimalSeparator = ",";
        hostile.NumberFormat.NegativeSign = "!";
        CultureInfo.CurrentCulture = hostile;
        CultureInfo.CurrentUICulture = hostile;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
    }
}

/// <summary>Sets env vars and restores their previous values (null = unset).</summary>
internal sealed class MetricsTracingEnvScope : IDisposable
{
    private readonly List<(string Name, string? Value)> _previous = new();

    public MetricsTracingEnvScope(params (string Name, string? Value)[] vars)
    {
        foreach (var (name, value) in vars)
        {
            _previous.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>
/// Empties the process-wide breaker registry for a test and puts back exactly
/// what was there. Other suites register live breakers into the same static.
/// </summary>
internal sealed class MetricsTracingBreakerRegistryScope : IDisposable
{
    private readonly IReadOnlyList<CircuitBreaker> _previous;

    public MetricsTracingBreakerRegistryScope()
    {
        _previous = CircuitBreakerRegistry.All;
        CircuitBreakerRegistry.ResetForTests();
    }

    public void Dispose()
    {
        CircuitBreakerRegistry.ResetForTests();
        foreach (var breaker in _previous)
            CircuitBreakerRegistry.Register(breaker);
    }
}

/// <summary>
/// Captures spans in-memory. Listens to EVERY ActivitySource rather than
/// filtering on Tracing.ActivitySourceName on purpose: that property is
/// recomputed from the live identity while the source object is frozen at type
/// load (see ChassisTracingSourceIdentityTests), so a name filter is not a
/// reliable way to see chassis spans from inside a shared test assembly.
/// Filtering happens on the span name instead.
/// </summary>
internal sealed class MetricsTracingSpanCapture : IDisposable
{
    private readonly ActivityListener _listener;

    public List<Activity> Started { get; } = new();

    public List<Activity> Stopped { get; } = new();

    public MetricsTracingSpanCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            // AllData: tags are recorded, nothing is marked for export.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => Started.Add(a),
            ActivityStopped = a => Stopped.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Activity? Stopped1(string name) => Stopped.FirstOrDefault(a => a.OperationName == name);

    public int StoppedCount(string name) => Stopped.Count(a => a.OperationName == name);

    public void Dispose() => _listener.Dispose();
}

// ── MetricsRenderer: family shape ────────────────────────────────────────────

public class MetricsRendererShapeTests
{
    [Fact]
    public void Counter_EmitsHelpTypeSample_PrefixedByTheConnectorId()
    {
        using var _ = new MetricsTracingIdentityScope("unit_probe");
        var sb = new StringBuilder();

        MetricsRenderer.Counter(sb, "docs_total", "Documents seen.", 42);

        // Exact bytes: Prometheus exposition is parsed line-by-line, and the
        // dashboards key off "<connector_id>_<name>". A changed prefix or a
        // reordered HELP/TYPE pair silently orphans every existing query.
        Assert.Equal(
            "# HELP unit_probe_docs_total Documents seen.\n"
            + "# TYPE unit_probe_docs_total counter\n"
            + "unit_probe_docs_total 42\n",
            sb.ToString());
    }

    [Fact]
    public void Renderers_UseBareLf_NeverCrLf()
    {
        using var _ = new MetricsTracingIdentityScope("unit_probe");
        var sb = new StringBuilder();

        MetricsRenderer.Counter(sb, "a_total", "A.", 1);
        MetricsRenderer.Gauge(sb, "b", "B.", 2);
        MetricsRenderer.GaugeDouble(sb, "c", "C.", 3.5);
        MetricsRenderer.LabeledCounter(sb, "d_total", "D.", "kind",
            new Dictionary<string, long> { ["x"] = 1 });

        // Swapping Append('\n') for AppendLine() would emit CRLF on Windows and
        // LF on Linux: the exposition format mandates LF, and the bug would
        // only ever reproduce on the Windows service hosts.
        Assert.DoesNotContain("\r", sb.ToString());
    }

    [Fact]
    public void Gauge_UsesTheGaugeType_AndMetric_PassesArbitraryTypesThrough()
    {
        using var _ = new MetricsTracingIdentityScope("unit_probe");
        var sb = new StringBuilder();

        MetricsRenderer.Gauge(sb, "queue_depth", "Queued items.", 7);
        MetricsRenderer.Metric(sb, "build_info", "Build info.", "untyped", "1");

        Assert.Equal(
            "# HELP unit_probe_queue_depth Queued items.\n"
            + "# TYPE unit_probe_queue_depth gauge\n"
            + "unit_probe_queue_depth 7\n"
            + "# HELP unit_probe_build_info Build info.\n"
            + "# TYPE unit_probe_build_info untyped\n"
            + "unit_probe_build_info 1\n",
            sb.ToString());
    }

    [Fact]
    public void Prefix_TracksTheLiveIdentity()
    {
        // Prefix is a computed property, not a cached field: a host that calls
        // Chassis.Init after some metric type has been touched must still get
        // its own series names.
        using (new MetricsTracingIdentityScope("first_connector"))
            Assert.Equal("first_connector_", MetricsRenderer.Prefix);

        using (new MetricsTracingIdentityScope("second_connector"))
            Assert.Equal("second_connector_", MetricsRenderer.Prefix);
    }
}

// ── MetricsRenderer: numeric formatting ──────────────────────────────────────

public class MetricsRendererCultureTests
{
    [Fact]
    public void AllNumericRenderers_StayInvariant_UnderACommaDecimalCulture()
    {
        using var identity = new MetricsTracingIdentityScope("unit_probe");
        using var culture = new MetricsTracingCultureScope();

        // Guard: if this fails the culture never took effect and the rest of
        // the test proves nothing.
        Assert.Equal("1,5", 1.5.ToString());
        Assert.Equal("!3", (-3L).ToString());

        var sb = new StringBuilder();
        MetricsRenderer.GaugeDouble(sb, "ratio", "Ratio.", 1.5);
        MetricsRenderer.Gauge(sb, "drift", "Drift.", -3);
        MetricsRenderer.Counter(sb, "items_total", "Items.", 1234567);
        MetricsRenderer.LabeledCounter(sb, "by_kind_total", "By kind.", "kind",
            new Dictionary<string, long> { ["a"] = -12 });

        var lines = sb.ToString().Split('\n');
        // Prometheus only accepts '.' as the decimal point and '-' as the sign.
        // Any of these picking up CurrentCulture makes the whole scrape fail to
        // parse on a non-en host — the classic .NET metrics defect.
        Assert.Contains("unit_probe_ratio 1.5", lines);
        Assert.Contains("unit_probe_drift -3", lines);
        Assert.Contains("unit_probe_items_total 1234567", lines);
        Assert.Contains("unit_probe_by_kind_total{kind=\"a\"} -12", lines);
    }

    [Fact]
    public void GaugeDouble_KeepsThreeDecimals_DropsTrailingZeros_AndFloorsBelowAMilli()
    {
        using var _ = new MetricsTracingIdentityScope("unit_probe");
        Assert.Equal("1.235", Render(1.23456));
        Assert.Equal("2", Render(2.0));
        Assert.Equal("-0.5", Render(-0.5));

        // Documented current behaviour, NOT an endorsement: the "0.###" format
        // has no significant-digit floor, so any gauge below 0.0005 renders as
        // a flat "0". A latency-seconds or error-ratio gauge loses the whole
        // signal. Reported as a finding; pinned here so the fix is deliberate.
        Assert.Equal("0", Render(0.0001));
    }

    [Fact]
    public void GaugeDouble_RendersNonFiniteValuesAsDotNetText_NotPrometheusTokens()
    {
        using var _ = new MetricsTracingIdentityScope("unit_probe");

        // NaN happens to be the token Prometheus expects. The infinities do
        // NOT: the exposition format wants "+Inf"/"-Inf", and .NET's invariant
        // symbol is "Infinity", which is a parse error for the whole scrape.
        // A ratio gauge computed as x/0 is all it takes. Reported as a finding;
        // this test records what the chassis does today.
        Assert.Equal("NaN", Render(double.NaN));
        Assert.Equal("Infinity", Render(double.PositiveInfinity));
        Assert.Equal("-Infinity", Render(double.NegativeInfinity));
    }

    private static string Render(double value)
    {
        var sb = new StringBuilder();
        MetricsRenderer.GaugeDouble(sb, "v", "V.", value);
        // "<prefix>v <value>\n" is the third line.
        return sb.ToString().Split('\n')[2]["unit_probe_v ".Length..];
    }
}

// ── MetricsRenderer: labels ──────────────────────────────────────────────────

public class MetricsRendererLabelTests
{
    [Fact]
    public void LabeledCounter_EmitsTheFamilyHeader_EvenWithNoSamples()
    {
        using var _ = new MetricsTracingIdentityScope("unit_probe");
        var sb = new StringBuilder();

        MetricsRenderer.LabeledCounter(sb, "errors_total", "Errors by kind.", "kind",
            new Dictionary<string, long>());

        // A family that vanishes when the counter is at zero makes a dashboard
        // read "no data" instead of "no errors", and breaks absent() alerting.
        Assert.Equal(
            "# HELP unit_probe_errors_total Errors by kind.\n"
            + "# TYPE unit_probe_errors_total counter\n",
            sb.ToString());
    }

    [Fact]
    public void LabeledCounter_OrdersSamplesOrdinally_NotByCulture()
    {
        using var identity = new MetricsTracingIdentityScope("unit_probe");
        using var culture = new MetricsTracingCultureScope();
        var sb = new StringBuilder();

        MetricsRenderer.LabeledCounter(sb, "by_kind_total", "By kind.", "kind",
            new Dictionary<string, long> { ["b"] = 1, ["A"] = 2, ["a"] = 3, ["B"] = 4 });

        // Ordinal puts every uppercase letter before every lowercase one; a
        // culture-aware comparer would interleave them (A,a,B,b). Stable
        // ordering is what keeps the /metrics page diffable and the golden
        // fixtures in the connector suites from flapping per-host.
        Assert.Equal(
            new[]
            {
                "unit_probe_by_kind_total{kind=\"A\"} 2",
                "unit_probe_by_kind_total{kind=\"B\"} 4",
                "unit_probe_by_kind_total{kind=\"a\"} 3",
                "unit_probe_by_kind_total{kind=\"b\"} 1",
            },
            sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2).ToArray());
    }

    [Fact]
    public void LabeledCounter_EscapesLabelValuesOnlyWhenAsked()
    {
        using var _ = new MetricsTracingIdentityScope("unit_probe");
        var values = new Dictionary<string, long> { ["a\"b"] = 1 };

        var escaped = new StringBuilder();
        MetricsRenderer.LabeledCounter(escaped, "x_total", "X.", "kind", values, escape: true);
        Assert.Contains("unit_probe_x_total{kind=\"a\\\"b\"} 1", escaped.ToString());

        // Default is verbatim: Clarizen and Hadoop rely on it, which means they
        // are responsible for only ever passing label values they control. An
        // untrusted value here produces the unparseable line below, so if a
        // connector ever starts feeding user data into a LabeledCounter it must
        // pass escape:true.
        var raw = new StringBuilder();
        MetricsRenderer.LabeledCounter(raw, "x_total", "X.", "kind", values);
        Assert.Contains("unit_probe_x_total{kind=\"a\"b\"} 1", raw.ToString());
    }

    [Fact]
    public void EscapeLabel_EscapesBackslashBeforeQuote_AndLeavesCarriageReturnRaw()
    {
        // Order matters: escaping the quote first would then double-escape the
        // backslash it introduces ("a\\\"b" instead of "a\\\\\"b" semantics).
        Assert.Equal(@"a\\b\""c", MetricsRenderer.EscapeLabel(@"a\b""c"));
        Assert.Equal("line\\nbreak", MetricsRenderer.EscapeLabel("line\nbreak"));
        Assert.Equal("", MetricsRenderer.EscapeLabel(""));

        // A lone CR is NOT escaped — current behaviour, pinned. Harmless for
        // the values the fleet actually emits (dependency names, HTTP status
        // classes) but a CRLF-bearing value from a Windows source would split
        // the sample line. Reported as a finding.
        Assert.Equal("a\rb", MetricsRenderer.EscapeLabel("a\rb"));
    }
}

// ── MetricsRenderer: circuit-breaker family ──────────────────────────────────

public class MetricsRendererCircuitBreakerTests
{
    [Fact]
    public void RenderCircuitBreakers_WithAnEmptyRegistry_RendersHeadersOnly()
    {
        using var identity = new MetricsTracingIdentityScope("unit_probe");
        using var registry = new MetricsTracingBreakerRegistryScope();
        var sb = new StringBuilder();

        // A connector that has not yet constructed its clients scrapes an empty
        // registry on the very first /metrics hit. That must be a valid,
        // sample-free page, not an exception that 500s the endpoint and takes
        // readiness down with it.
        MetricsRenderer.RenderCircuitBreakers(sb);

        Assert.Equal(
            new[]
            {
                "# HELP unit_probe_circuit_breaker_state Circuit-breaker state per dependency (0=closed, 1=open, 2=half-open).",
                "# TYPE unit_probe_circuit_breaker_state gauge",
                "# HELP unit_probe_circuit_breaker_trips_total Times a dependency breaker opened (closed→open).",
                "# TYPE unit_probe_circuit_breaker_trips_total counter",
                "# HELP unit_probe_circuit_breaker_resets_total Times a dependency breaker recovered (half-open→closed).",
                "# TYPE unit_probe_circuit_breaker_resets_total counter",
            },
            sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task RenderCircuitBreakers_ReadsStateTripsAndResetsFromDistinctFields()
    {
        using var identity = new MetricsTracingIdentityScope("unit_probe");
        using var registry = new MetricsTracingBreakerRegistryScope();

        // "alpha" is driven closed -> open -> half-open -> closed on a fake
        // clock, so its three numbers are all different (state 0, trips 1,
        // resets 1). A copy-paste regression in the renderer — printing
        // TripCount under the resets family, say — cannot hide behind equal
        // values the way it would with a pair of untouched breakers.
        var now = DateTimeOffset.UnixEpoch;
        var alpha = new CircuitBreaker(
            "alpha",
            new CircuitBreakerOptions { FailureThreshold = 1, OpenDuration = TimeSpan.FromSeconds(30) },
            critical: true,
            clock: () => now);
        CircuitBreakerRegistry.Register(alpha);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            alpha.ExecuteAsync<int>(_ => throw new InvalidOperationException("down"), _ => true));
        Assert.Equal(CircuitState.Open, alpha.State);
        now = now.AddSeconds(31);
        Assert.Equal(CircuitState.HalfOpen, alpha.State);
        await alpha.ExecuteAsync(_ => Task.FromResult(0), _ => true);
        Assert.Equal(CircuitState.Closed, alpha.State);

        // A name needing escaping, held open: proves the dependency label is
        // escaped in all three families, not just the first.
        var weird = CircuitBreakerRegistry.Register(new CircuitBreaker("we\"ird"));
        weird.ForceOpenForTests();

        var sb = new StringBuilder();
        MetricsRenderer.RenderCircuitBreakers(sb);
        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Samples ordered by dependency name (ordinal), state encoded as the
        // documented 0/1/2 the dashboards decode.
        Assert.Equal("unit_probe_circuit_breaker_state{dependency=\"alpha\"} 0", lines[2]);
        Assert.Equal("unit_probe_circuit_breaker_state{dependency=\"we\\\"ird\"} 1", lines[3]);
        Assert.Equal("unit_probe_circuit_breaker_trips_total{dependency=\"alpha\"} 1", lines[6]);
        Assert.Equal("unit_probe_circuit_breaker_trips_total{dependency=\"we\\\"ird\"} 1", lines[7]);
        Assert.Equal("unit_probe_circuit_breaker_resets_total{dependency=\"alpha\"} 1", lines[10]);
        Assert.Equal("unit_probe_circuit_breaker_resets_total{dependency=\"we\\\"ird\"} 0", lines[11]);
    }
}

// ── Tracing: options / initialization ────────────────────────────────────────

public class ChassisTracingInitializeTests
{
    [Fact]
    public void EnvVarNames_AreTheStandardOtelSpellings()
    {
        // Every host builds ChassisTracingOptions by reading these two names.
        // Renaming either one disables export fleet-wide and silently: nothing
        // throws, the collector simply never hears from the connector again.
        Assert.Equal("OTEL_EXPORTER_OTLP_ENDPOINT", Tracing.EndpointEnvVar);
        Assert.Equal("OTEL_SERVICE_NAME", Tracing.ServiceNameEnvVar);
    }

    [Fact]
    public void Initialize_WithNoEndpoint_InstallsNoExporter_ButStillAppliesTheServiceName()
    {
        var originalServiceName = Tracing.ServiceName;
        // Unset endpoint + a service name, exactly the shape the hosts resolve
        // from the environment (see SeismicTracing.OptionsFrom).
        using var env = new MetricsTracingEnvScope(
            (Tracing.EndpointEnvVar, null),
            (Tracing.ServiceNameEnvVar, "chassis-tests-service"));
        try
        {
            var endpoint = Environment.GetEnvironmentVariable(Tracing.EndpointEnvVar);
            var options = new ChassisTracingOptions(
                Enabled: !string.IsNullOrWhiteSpace(endpoint),
                OtlpEndpoint: endpoint,
                OtlpProtocol: null,
                OtlpHeaders: null,
                ServiceName: Environment.GetEnvironmentVariable(Tracing.ServiceNameEnvVar)!);

            Assert.False(options.Enabled);
            Tracing.Initialize(options);

            // No endpoint => no TracerProvider, no listener, nothing to flush.
            Assert.False(Tracing.Enabled);
            Assert.Null(Tracing.ExporterEndpoint);
            // ...but the service name is applied regardless, because it also
            // names the spans a locally-attached listener sees.
            Assert.Equal("chassis-tests-service", Tracing.ServiceName);

            // Shutdown on the disabled path is a documented no-op, and must
            // stay one: the hosts call it unconditionally on graceful stop.
            Tracing.Shutdown();
            Tracing.Shutdown();
            Assert.False(Tracing.Enabled);
        }
        finally
        {
            // ServiceName has no public setter; Initialize is the only way
            // back, and it works here precisely because the disabled path left
            // no provider installed (Initialize early-returns when one exists).
            Tracing.Initialize(new ChassisTracingOptions(false, null, null, null, originalServiceName));
        }
    }

    [Fact]
    public void Initialize_WithEnabledButBlankEndpoint_StaysInert()
    {
        var originalServiceName = Tracing.ServiceName;
        try
        {
            // A host that sets OTEL_EXPORTER_OTLP_ENDPOINT="   " would otherwise
            // reach `new Uri("   ")`. The whitespace guard is what keeps that
            // from being a startup failure path at all.
            Tracing.Initialize(new ChassisTracingOptions(
                Enabled: true, OtlpEndpoint: "   ", OtlpProtocol: "grpc",
                OtlpHeaders: "x=y", ServiceName: "chassis-tests-blank"));

            Assert.False(Tracing.Enabled);
            Assert.Null(Tracing.ExporterEndpoint);
            Assert.Equal("chassis-tests-blank", Tracing.ServiceName);
        }
        finally
        {
            Tracing.Initialize(new ChassisTracingOptions(false, null, null, null, originalServiceName));
        }
    }

    [Fact]
    public void ChassisTracingOptions_IsAValueType_SoHostsCanCompareResolvedConfig()
    {
        var a = new ChassisTracingOptions(true, "http://c:4318", "http/protobuf", null, "svc");
        var b = new ChassisTracingOptions(true, "http://c:4318", "http/protobuf", null, "svc");
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { OtlpEndpoint = "http://c:4317" });
    }
}

// ── Tracing: correlation ─────────────────────────────────────────────────────

public class ChassisTracingCorrelationTests
{
    [Fact]
    public void BeginCycle_YieldsAUsableCorrelationId_EvenWithExportOff()
    {
        var ambient = Tracing.CurrentCorrelationId;
        try
        {
            using (var scope = Tracing.BeginCycle("unit_probe", "full"))
            {
                // Correlation is NOT gated on the exporter: with no endpoint
                // configured the id is a fresh Guid("N"), with a listener it is
                // the W3C trace id. Both are 32 lowercase hex chars, and every
                // log line / dead-letter record / report entry keys off it, so
                // an empty id silently destroys the audit trail without
                // breaking anything loudly.
                Assert.Equal(32, scope.CorrelationId.Length);
                Assert.All(scope.CorrelationId, c => Assert.True(
                    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    $"correlation id is not lowercase hex: '{scope.CorrelationId}'"));
                Assert.Equal(scope.CorrelationId, Tracing.CurrentCorrelationId);
            }

            Assert.Equal(ambient, Tracing.CurrentCorrelationId);
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }

    [Fact]
    public void BeginCycle_NestsAndUnwindsTheCorrelationIdLikeAStack()
    {
        var ambient = Tracing.CurrentCorrelationId;
        try
        {
            using var outer = Tracing.BeginCycle("unit_probe", "full");
            var outerId = Tracing.CurrentCorrelationId;

            using (var inner = Tracing.BeginCycle("unit_probe", "delta"))
            {
                Assert.NotEqual(outerId, inner.CorrelationId);
                Assert.Equal(inner.CorrelationId, Tracing.CurrentCorrelationId);
            }

            // Restoring the PREVIOUS id (not clearing the slot) is what lets a
            // sweep run inside a cycle without orphaning the cycle's later log
            // lines from its trace.
            Assert.Equal(outerId, Tracing.CurrentCorrelationId);
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }

    [Fact]
    public void BeginNamedScope_InheritsTheAmbientCorrelationId_OrMintsOne()
    {
        var ambient = Tracing.CurrentCorrelationId;
        try
        {
            using (var cycle = Tracing.BeginCycle("unit_probe", "full"))
            using (var sweep = Tracing.BeginNamedScope("reconcile.sweep", "unit_probe", "reconcile"))
            {
                // Sweeps open their own root span but must NOT mint a second
                // id, or the sweep's records stop joining the cycle they ran in.
                Assert.Equal(cycle.CorrelationId, sweep.CorrelationId);
            }

            Tracing.ResetCorrelationForTests();
            using (var standalone = Tracing.BeginNamedScope("webhook.handle", "unit_probe", "webhook"))
                Assert.False(string.IsNullOrWhiteSpace(standalone.CorrelationId));
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }

    [Fact]
    public void CycleScope_DisposeIsIdempotent()
    {
        var ambient = Tracing.CurrentCorrelationId;
        try
        {
            var scope = Tracing.BeginCycle("unit_probe", "full");
            scope.Dispose();
            var afterFirst = Tracing.CurrentCorrelationId;

            // Hosts wrap cycles in `using` AND call Dispose on some error
            // paths. A second Dispose must not re-restore a now-stale previous
            // id over whatever the caller established in between.
            Tracing.SetCorrelationForTests("deadbeef");
            scope.Dispose();
            Assert.Equal("deadbeef", Tracing.CurrentCorrelationId);
            Assert.Equal(ambient, afterFirst);
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }

    [Fact]
    public void SetTagAndSetError_AreSafeWhenTheSpanIsInert()
    {
        var ambient = Tracing.CurrentCorrelationId;
        try
        {
            // No listener is guaranteed here, so the scope may hold a null
            // Activity. Every call on it must still be a no-op rather than an
            // NRE on a crawl-failure path — which is exactly when SetError runs.
            using var scope = Tracing.BeginCycle("unit_probe", "full");
            scope.SetTag("items", 5);
            scope.SetTag("null", null);
            scope.SetError("boom");
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }
}

// ── Tracing: spans actually emitted ──────────────────────────────────────────

public class ChassisTracingSpanTests
{
    private static string? Tag(Activity a, string key) => a.GetTagItem(key)?.ToString();

    [Fact]
    public void BeginCycle_EmitsACrawlCycleSpan_TaggedAndTiedToTheCorrelationId()
    {
        var ambient = Tracing.CurrentCorrelationId;
        using var capture = new MetricsTracingSpanCapture();
        try
        {
            string correlationId;
            using (var scope = Tracing.BeginCycle("unit_probe", "full", objectFilter: "ContentItem"))
            {
                correlationId = scope.CorrelationId;
                Assert.NotNull(Activity.Current);
            }

            // The span is real, not a reference to the OpenTelemetry package: a
            // listener saw it start and stop on the chassis ActivitySource.
            var cycle = capture.Stopped1("crawl.cycle");
            Assert.NotNull(cycle);
            Assert.Equal(ActivityKind.Internal, cycle!.Kind);
            Assert.Equal("unit_probe", Tag(cycle, "connector.id"));
            Assert.Equal("full", Tag(cycle, "seismic.crawl_kind"));
            Assert.Equal("ContentItem", Tag(cycle, "seismic.object_filter"));

            // With a span present the correlation id IS the W3C trace id, which
            // is what makes a log line searchable in the trace backend.
            Assert.Equal(cycle.TraceId.ToHexString(), correlationId);
            Assert.Equal(correlationId, Tag(cycle, "correlation_id"));
            Assert.True(cycle.Duration >= TimeSpan.Zero);
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }

    [Fact]
    public void BeginCycle_OmitsTheObjectFilterTag_WhenNoFilterIsGiven()
    {
        var ambient = Tracing.CurrentCorrelationId;
        using var capture = new MetricsTracingSpanCapture();
        try
        {
            using (Tracing.BeginCycle("unit_probe", "delta")) { }

            // Absent, not empty-string: an "" filter tag would read as "filtered
            // to nothing" in the trace UI.
            var cycle = capture.Stopped1("crawl.cycle")!;
            Assert.Null(cycle.GetTagItem("seismic.object_filter"));
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }

    [Fact]
    public void NestedScopes_FormAParentChildTree_UnderOneTraceId()
    {
        var ambient = Tracing.CurrentCorrelationId;
        using var capture = new MetricsTracingSpanCapture();
        try
        {
            using (Tracing.BeginCycle("unit_probe", "full"))
            using (Tracing.BeginNamedScope("reconcile.sweep", "unit_probe", "reconcile"))
            using (Tracing.StartActivity("graph.batch_ingest")) { }

            var cycle = capture.Stopped1("crawl.cycle")!;
            var sweep = capture.Stopped1("reconcile.sweep")!;
            var batch = capture.Stopped1("graph.batch_ingest")!;

            // Parenting comes from Activity.Current, so a scope that forgets to
            // dispose (or disposes early) reparents everything after it and the
            // waterfall view stops reflecting the real call structure.
            Assert.Equal(cycle.SpanId, sweep.ParentSpanId);
            Assert.Equal(sweep.SpanId, batch.ParentSpanId);
            Assert.Equal(cycle.TraceId, batch.TraceId);
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }

    [Fact]
    public void CycleScope_StopsItsSpanExactlyOnce_EvenOnDoubleDispose()
    {
        var ambient = Tracing.CurrentCorrelationId;
        using var capture = new MetricsTracingSpanCapture();
        try
        {
            var scope = Tracing.BeginCycle("unit_probe", "full");
            scope.Dispose();
            scope.Dispose();

            // A second stop would emit the span twice to the exporter and
            // double-count every span-derived metric.
            Assert.Equal(1, capture.StoppedCount("crawl.cycle"));
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }

    [Fact]
    public void SetError_MarksTheSpanFailed()
    {
        var ambient = Tracing.CurrentCorrelationId;
        using var capture = new MetricsTracingSpanCapture();
        try
        {
            using (var scope = Tracing.BeginCycle("unit_probe", "full"))
                scope.SetError("graph rejected the batch");

            var cycle = capture.Stopped1("crawl.cycle")!;
            // Error status is what turns a red trace red in the backend; losing
            // it makes a failed crawl indistinguishable from a clean one.
            Assert.Equal(ActivityStatusCode.Error, cycle.Status);
            Assert.Equal("graph rejected the batch", cycle.StatusDescription);
        }
        finally
        {
            Tracing.SetCorrelationForTests(ambient);
        }
    }
}

// ── Tracing: W3C context propagation ─────────────────────────────────────────

public class ChassisTracingContextInjectionTests
{
    [Fact]
    public void InjectTraceContext_WritesTheCurrentTraceparent_AndNeverOverwritesOne()
    {
        using var capture = new MetricsTracingSpanCapture();
        using var span = Tracing.StartActivity("outbound");
        Assert.NotNull(span);

        var fresh = new HttpRequestMessage(HttpMethod.Get, "http://localhost/never-sent");
        Tracing.InjectTraceContext(fresh);
        // W3C ids are forced in Tracing's static ctor, so the header is the
        // span id downstream services will parse. Without this the trace stops
        // at the connector boundary.
        Assert.Equal(span!.Id, Assert.Single(fresh.Headers.GetValues("traceparent")));

        var preset = new HttpRequestMessage(HttpMethod.Get, "http://localhost/never-sent");
        preset.Headers.TryAddWithoutValidation("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        Tracing.InjectTraceContext(preset);
        // Caller-supplied context wins: retried requests already carry one, and
        // a duplicated traceparent header is a protocol error.
        Assert.Equal(
            "00-11111111111111111111111111111111-2222222222222222-01",
            Assert.Single(preset.Headers.GetValues("traceparent")));
    }

    [Fact]
    public void InjectTraceContext_IsANoOp_WithNoActiveSpan()
    {
        var previous = Activity.Current;
        try
        {
            Activity.Current = null;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/never-sent");

            // The whole point of the opt-in design: with tracing off, outbound
            // requests must go out byte-identical to the untraced connector.
            Tracing.InjectTraceContext(request);
            Assert.False(request.Headers.Contains("traceparent"));
            Assert.False(request.Headers.Contains("tracestate"));
        }
        finally
        {
            Activity.Current = previous;
        }
    }
}

// ── Tracing: the ActivitySource name is frozen at type load ──────────────────

public class ChassisTracingSourceIdentityTests
{
    private const string RenamedSource = "Connector.Chassis.Tests.MetricsAndTracing.RenamedSource";

    [Fact]
    public void ActivitySourceName_FollowsTheIdentity_ButTheSourceObjectDoesNot()
    {
        using var capture = new MetricsTracingSpanCapture();

        // Emit under the ambient identity first: this both forces the type
        // initializer and tells us which name the single static ActivitySource
        // was constructed with.
        string frozen;
        using (var probe = Tracing.StartActivity("chassis.tests.source_probe"))
        {
            Assert.NotNull(probe);
            frozen = probe!.Source.Name;
        }

        var previous = Chassis.Identity;
        try
        {
            Chassis.Init(new ChassisIdentity("renamed_probe", RenamedSource, "renamed_probe"));

            // The PROPERTY is recomputed from the live identity...
            Assert.Equal(RenamedSource, Tracing.ActivitySourceName);

            using var after = Tracing.StartActivity("chassis.tests.source_probe");
            Assert.NotNull(after);
            // ...but the ActivitySource is a static readonly built once, so
            // spans keep coming from the old name. Documented current
            // behaviour, reported as a finding: any listener or OTLP pipeline
            // wired with `AddSource(Tracing.ActivitySourceName)` AFTER a late
            // Chassis.Init silently receives nothing.
            Assert.Equal(frozen, after!.Source.Name);
            Assert.NotEqual(Tracing.ActivitySourceName, after.Source.Name);
        }
        finally
        {
            Chassis.Init(previous);
        }
    }
}
