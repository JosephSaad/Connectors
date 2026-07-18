// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Live console dashboard for the ingestion pipeline.

using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SalesforceCopilotConnector;

/// <summary>Module-level constants of <c>dashboard.py</c>.</summary>
public static class Dashboard
{
    /// <summary>
    /// Python sets <c>HAS_RICH</c> based on whether ``rich`` is importable;
    /// Spectre.Console is always available in the C# port.
    /// </summary>
    public const bool HasRich = true;
}

/// <summary>
/// Thread-safe live dashboard powered by Spectre.Console (rich in Python).
///
/// A background render thread rebuilds the display four times per second,
/// mirroring ``rich.live.Live(refresh_per_second=4, transient=True)``.
/// </summary>
// Not sealed, and StopRequested / ChunkIngested are virtual, so tests can substitute a
// fake dashboard (the Python tests use a MagicMock dashboard to simulate Ctrl+X).
public class IngestionDashboard
{
    private sealed class Obj
    {
        public int Expected;
        public int Fetched;
        public int Ingested;
        public int Failed;
        public int Chunk;
        public string Status = "pending";
        public double T0;
    }

    /// <summary>Cumulative timing for one phase of one object type.</summary>
    private sealed class PhaseTiming
    {
        public double TotalSecs;
        public int Count;  // number of chunks/calls

        public double AvgSecs => Count != 0 ? TotalSecs / Count : 0.0;
    }

    private static readonly string[] Phases = { "SF Fetch", "ACL", "Transform", "Graph Push" };

    private static readonly Dictionary<string, string> AclStyles = new()
    {
        ["Private"] = "red",
        ["Public Read"] = "green",
        ["Public Read/Write"] = "green",
        ["Public Read/Write/Transfer"] = "green",
        ["ControlledByParent"] = "yellow",
        ["None"] = "dim",
    };

    private readonly string _cid;
    private readonly string _mode;
    private readonly string _acl;
    private readonly string _log;
    private readonly string _failedLog;
    private readonly Dictionary<string, Obj> _objs = new();
    private readonly List<string> _order = new();
    private readonly Dictionary<string, string> _aclTypes = new();  // {object_type: "Private", ...}
    private string _activity = "Initializing...";
    private double _activityT0;
    private List<string> _errors = new();
    private string _lastError = "";
    private Dictionary<string, int> _totalCounts = new();
    private volatile bool _stopRequested;
    private readonly double _t0;
    private readonly Queue<(double Time, int Count)> _rateWindow = new();
    private double _lastIngestTime;
    private double _frozenRate;
#pragma warning disable CS0414  // kept for parity with Python's _force_exit (also write-only)
    private bool _forceExit;
#pragma warning restore CS0414
    private double _lastAclDuration;  // seconds the last ACL resolution took
    private readonly object _lock = new();
    // Per-object phase timings: {obj_type: {phase: PhaseTiming}}
    private readonly Dictionary<string, Dictionary<string, PhaseTiming>> _timings = new();

    private Thread? _liveThread;
    private ManualResetEventSlim? _liveStop;

    public IngestionDashboard(string connectorId, string syncMode, string aclEngine, string logFile, string failedFile = "")
    {
        _cid = connectorId;
        _mode = syncMode;
        _acl = aclEngine;
        _log = logFile;
        _failedLog = failedFile;
        _t0 = Now();
        _activityT0 = _t0;
    }

    // -- Helpers ----------------------------------------------------------------

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static string FmtDur(double seconds)
    {
        var inv = CultureInfo.InvariantCulture;
        if (seconds < 60)
            return seconds.ToString("F0", inv) + "s";
        var m = (int)seconds / 60;
        var s = (int)seconds % 60;
        if (m < 60)
            return string.Format(inv, "{0}m {1:D2}s", m, s);
        var h = m / 60;
        m %= 60;
        return string.Format(inv, "{0}h {1:D2}m {2:D2}s", h, m, s);
    }

    private static string FmtInt(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    // -- Lifecycle ------------------------------------------------------------

    public virtual bool StopRequested => _stopRequested;

    public void Start()
    {
        _liveStop = new ManualResetEventSlim(false);
        var stop = _liveStop;
        _liveThread = new Thread(() =>
        {
            try
            {
                AnsiConsole.Live(new Text(""))
                    .AutoClear(true)                    // rich transient=True
                    .Overflow(VerticalOverflow.Crop)    // vertical_overflow="crop"
                    .Start(ctx =>
                    {
                        while (!stop.IsSet)
                        {
                            IRenderable renderable;
                            lock (_lock)
                                renderable = Build();
                            ctx.UpdateTarget(renderable);
                            ctx.Refresh();
                            Thread.Sleep(250);          // refresh_per_second=4
                        }
                    });
            }
            catch
            {
                // best-effort rendering
            }
        })
        {
            IsBackground = true,
            Name = "dashboard-live",
        };
        _liveThread.Start();
        StartKeyMonitor();
    }

    public void Stop()
    {
        if (_liveThread != null)
        {
            _liveStop?.Set();
            try
            {
                _liveThread.Join(2000);
            }
            catch
            {
                // Best-effort join during console teardown — the render thread is
                // a daemon; a Join fault must not block command completion.
            }
            _liveThread = null;
        }
    }

    // -- Update API -----------------------------------------------------------

    private Obj GetObj(string name)
    {
        if (!_objs.TryGetValue(name, out var o))
        {
            o = new Obj();
            _objs[name] = o;
            _order.Add(name);
        }
        return o;
    }

    public void SetObjectTypes(List<string> types)
    {
        lock (_lock)
        {
            foreach (var t in types)
                GetObj(t);
        }
    }

    public void SetTotalCounts(Dictionary<string, int> counts)
    {
        lock (_lock)
        {
            _totalCounts = new Dictionary<string, int>(counts);
            foreach (var (name, count) in counts)
                GetObj(name).Expected = count;
        }
    }

    /// <summary>Set the OWD / ACL visibility label for each object type.</summary>
    public void SetAclTypes(Dictionary<string, string> aclMap)
    {
        lock (_lock)
        {
            foreach (var (key, value) in aclMap)
                _aclTypes[key] = value;
        }
    }

    public void ChunkFetched(string objType, int chunkIdx, int count)
    {
        lock (_lock)
        {
            var o = GetObj(objType);
            if (o.T0 == 0.0)
                o.T0 = Now();
            o.Fetched += count;
            o.Chunk = chunkIdx;
            o.Status = "fetching";
            _activity = $"[{objType}] chunk #{chunkIdx} -- fetched {count} records";
            _activityT0 = Now();
        }
    }

    public void ChunkSkipped(string objType, int count)
    {
        lock (_lock)
        {
            var o = GetObj(objType);
            o.Fetched += count;
            o.Ingested += count;
        }
    }

    public void AclStarted(string objType, int chunkIdx, int count)
    {
        lock (_lock)
        {
            if (_objs.TryGetValue(objType, out var o))
            {
                o.Status = "acl";
                o.Chunk = chunkIdx;
            }
            _activity = $"[{objType}] chunk #{chunkIdx} -- Resolving ACLs ({count} records)";
            _activityT0 = Now();
        }
    }

    public void SetActivity(string msg)
    {
        lock (_lock)
        {
            // If we're leaving an ACL phase, record how long it took
            if (_activity.Contains("Resolving ACLs") && !msg.Contains("Resolving ACLs"))
                _lastAclDuration = Now() - _activityT0;
            _activity = msg;
            _activityT0 = Now();
        }
    }

    public virtual void ChunkIngested(string objType, int success, int failed)
    {
        lock (_lock)
        {
            if (_objs.TryGetValue(objType, out var o))
            {
                o.Ingested += success;
                o.Failed += failed;
                o.Status = "ingesting";
            }
            if (success > 0)
                _lastIngestTime = Now();
        }
    }

    public void ObjectDone(string objType)
    {
        lock (_lock)
        {
            if (_objs.TryGetValue(objType, out var o))
                o.Status = "done";
        }
    }

    /// <summary>Record elapsed time for a pipeline phase (called from the ingest pipeline).</summary>
    public void RecordPhaseTime(string objType, string phase, double duration)
    {
        lock (_lock)
        {
            if (!_timings.TryGetValue(objType, out var objTimings))
            {
                objTimings = new Dictionary<string, PhaseTiming>();
                _timings[objType] = objTimings;
            }
            if (!objTimings.TryGetValue(phase, out var pt))
            {
                pt = new PhaseTiming();
                objTimings[phase] = pt;
            }
            pt.TotalSecs += duration;
            pt.Count += 1;
        }
    }

    public void AddError(string msg)
    {
        lock (_lock)
        {
            _errors.Add(msg);
            _lastError = msg;
            if (_errors.Count > 8)
                _errors = _errors.Skip(_errors.Count - 8).ToList();
        }
    }

    private void StartKeyMonitor()
    {
        var t = new Thread(KeyLoop)
        {
            IsBackground = true,
            Name = "ctrl-x-monitor",
        };
        t.Start();
    }

    private void KeyLoop()
    {
        try
        {
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.KeyChar == '\x18' ||
                        (key.Key == ConsoleKey.X && key.Modifiers.HasFlag(ConsoleModifiers.Control)))  // Ctrl+X
                    {
                        if (_stopRequested)
                        {
                            DoForceExit();
                            return;
                        }
                        DoStop();
                    }
                }
                Thread.Sleep(100);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void DoStop()
    {
        lock (_lock)
        {
            _stopRequested = true;
            _activity = "Ctrl+X pressed -- stopping after current chunk...";
            _activityT0 = Now();
        }
    }

    private void DoForceExit()
    {
        lock (_lock)
        {
            _forceExit = true;
            _activity = "Ctrl+X x2 -- ingestion stopped abruptly";
            _activityT0 = Now();
        }
        // Stop dashboard cleanly then exit
        Stop();
        Console.WriteLine("\n  Ingestion stopped abruptly by user (Ctrl+X x2).");
        Console.WriteLine("  Progress was checkpointed -- next run will resume.\n");
        Environment.Exit(1);
    }

    public void Finish()
    {
        lock (_lock)
        {
            foreach (var o in _objs.Values)
            {
                if (o.Fetched > 0)
                    o.Status = "done";
            }
            _activity = "Complete";
            _activityT0 = Now();
        }
    }

    // -- Rendering ------------------------------------------------------------

    /// <summary>Items/sec based on a 2-minute sliding window for stable ETA.</summary>
    private double RollingRate(double now, int totI)
    {
        _rateWindow.Enqueue((now, totI));
        var cutoff = now - 120;
        while (_rateWindow.Count > 0 && _rateWindow.Peek().Time < cutoff)
            _rateWindow.Dequeue();
        if (_rateWindow.Count >= 2)
        {
            var (t0, c0) = _rateWindow.Peek();
            var dt = now - t0;
            if (dt > 1)
                return (totI - c0) / dt;
        }
        // Fallback to overall rate for the first 2 seconds
        var elapsed = now - _t0;
        return elapsed > 0.5 ? totI / elapsed : 0;
    }

    private static string Pct1(double fraction) =>
        (fraction * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string Pct0(double fraction) =>
        (fraction * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private IRenderable Build()
    {
        var inv = CultureInfo.InvariantCulture;
        var now = Now();
        var totI = _objs.Values.Sum(o => o.Ingested);
        var totFail = _objs.Values.Sum(o => o.Failed);
        var elapsed = now - _t0;
        var grandTotal = _totalCounts.Count > 0 ? _totalCounts.Values.Sum() : _objs.Values.Sum(o => o.Fetched);

        // Rate freezes during ACL pauses (no items flowing) so ETA stays stable
        var idleSecs = _lastIngestTime > 0 ? now - _lastIngestTime : 0;
        if (idleSecs < 10)
        {
            // Items are actively flowing — compute live rate and save it
            _frozenRate = RollingRate(now, totI);
        }
        // else: keep _frozenRate from last active push window
        var overallRate = _frozenRate;

        // -- Header -----------------------------------------------------------
        var totFailAny = _objs.Values.Sum(o => o.Failed);
        var headerLines =
            $"[bold]Connector:[/] {Markup.Escape(_cid)}  [dim]|[/]  " +
            $"[bold]Mode:[/] {Markup.Escape(_mode)}  [dim]|[/]  " +
            $"[bold]ACL:[/] {Markup.Escape(_acl)}\n" +
            $"[dim]Log:     {Markup.Escape(_log)}[/]";
        if (!string.IsNullOrEmpty(_failedLog))
        {
            var style = totFailAny > 0 ? "red" : "dim";
            headerLines += $"\n[{style}]Errors:  {Markup.Escape(_failedLog)}[/]";
        }
        var header = new Panel(new Markup(headerLines))
        {
            Header = new PanelHeader("[bold blue] Salesforce >> Graph Ingestion [/]"),
            Padding = new Padding(1, 0, 1, 0),
        };
        header.BorderColor(Color.Blue);

        // -- Object table -----------------------------------------------------
        var tbl = new Table().Expand();
        tbl.BorderColor(Color.Grey);
        tbl.AddColumn(new TableColumn("[bold]Object[/]").NoWrap());
        tbl.AddColumn(new TableColumn("[bold]ACL[/]").NoWrap());
        tbl.AddColumn(new TableColumn("[bold]Ingested / Total[/]").RightAligned().NoWrap());
        tbl.AddColumn(new TableColumn("[bold]Failed[/]").RightAligned().NoWrap());
        tbl.AddColumn(new TableColumn("[bold]ETA[/]").RightAligned().NoWrap());
        tbl.AddColumn(new TableColumn("[bold]Status[/]").NoWrap());

        foreach (var name in _order)
        {
            var o = _objs[name];
            var objTotal = o.Expected != 0 ? o.Expected : o.Fetched;
            var isPending = o.Fetched == 0;

            // -- ingested / total --
            string countCell;
            if (isPending && o.Expected > 0)
                countCell = $"[dim]- / {FmtInt(o.Expected)}[/]";
            else if (isPending)
                countCell = "[dim]-[/]";
            else
                countCell = $"{FmtInt(o.Ingested)} / {FmtInt(objTotal)}";

            // -- per-object ETA --
            // Pending objects: show a static estimate (frozen rate), dimmed
            // Active objects: use their own measured rate
            // Done objects: no ETA
            string objEta;
            if (o.Status == "done")
                objEta = "[dim]-[/]";
            else if (o.Status == "pending" && overallRate > 0 && objTotal > 0)
                objEta = $"[dim]~{FmtDur(objTotal / overallRate)}[/]";
            else if (o.T0 > 0 && o.Ingested > 0 && objTotal > o.Ingested)
            {
                var objRate = o.Ingested / (now - o.T0);
                objEta = $"~{FmtDur((objTotal - o.Ingested) / objRate)}";
            }
            else if (overallRate > 0 && objTotal > o.Ingested)
                objEta = $"~{FmtDur((objTotal - o.Ingested) / overallRate)}";
            else
                objEta = "[dim]-[/]";

            // -- status --
            string status;
            if (o.Status == "ingesting")
                status = $"[green]> Chunk #{o.Chunk}[/]";
            else if (o.Status == "acl")
                status = $"[yellow]~ ACL #{o.Chunk}[/]";
            else if (o.Status == "fetching")
                status = "[yellow]v Fetching[/]";
            else if (o.Status == "done")
            {
                var skipped = Math.Max(0, o.Fetched - o.Ingested - o.Failed);
                status = skipped > 0
                    ? $"[bold green]+ Done[/] [dim]({skipped} skip)[/]"
                    : "[bold green]+ Done[/]";
            }
            else
                status = "[dim]- Pending[/]";

            // -- ACL type label --
            var aclLabel = _aclTypes.TryGetValue(name, out var al) ? al : "";
            var aclStyle = AclStyles.TryGetValue(aclLabel, out var st) ? st : "dim";
            var aclCell = aclLabel.Length > 0 ? $"[{aclStyle}]{Markup.Escape(aclLabel)}[/]" : "[dim]-[/]";

            var failStyle = o.Failed > 0 ? "red" : "dim";
            tbl.AddRow(
                $"[cyan]{Markup.Escape(name)}[/]",
                aclCell,
                countCell,
                $"[{failStyle}]{(isPending ? "-" : o.Failed.ToString(inv))}[/]",
                objEta,
                status);
        }

        // -- Totals --
        tbl.AddRow(
            "[bold]Total[/]",
            "",
            grandTotal > 0 ? $"[bold]{FmtInt(totI)} / {FmtInt(grandTotal)}[/]" : $"[bold]{FmtInt(totI)}[/]",
            $"[{(totFail > 0 ? "bold red" : "bold dim")}]{totFail.ToString(inv)}[/]",
            "",
            "");

        // -- Overall progress bar ---------------------------------------------
        IRenderable barGrid;
        if (grandTotal > 0)
        {
            var pct = Math.Min((double)totI / grandTotal, 1.0);
            const int barWidth = 50;
            var filled = Math.Min(barWidth, (int)Math.Round(pct * barWidth));
            var bar =
                (filled > 0 ? $"[green]{new string('━', filled)}[/]" : "") +
                (barWidth - filled > 0 ? $"[grey]{new string('━', barWidth - filled)}[/]" : "");
            barGrid = new Markup($" {bar}  {FmtInt(totI)} / {FmtInt(grandTotal)}  ({Pct1(pct)})");
        }
        else
        {
            barGrid = new Markup("[dim]  Waiting for records...[/]");
        }

        // -- Timing breakdown table --------------------------------------------
        Table? timingTbl = null;
        if (_timings.Count > 0)
        {
            timingTbl = new Table().Expand();
            timingTbl.BorderColor(Color.Grey);
            timingTbl.Title = new TableTitle("[bold]Phase Timing (cumulative)[/]");
            timingTbl.AddColumn(new TableColumn("[bold]Object[/]").NoWrap());
            foreach (var phase in Phases)
                timingTbl.AddColumn(new TableColumn($"[bold]{phase}[/]").RightAligned().NoWrap());
            timingTbl.AddColumn(new TableColumn("[bold]Total[/]").RightAligned().NoWrap());

            var grandPhase = Phases.ToDictionary(p => p, _ => 0.0);
            foreach (var name in _order)
            {
                var objTimings = _timings.TryGetValue(name, out var t) ? t : new Dictionary<string, PhaseTiming>();
                var cells = new List<string>();
                var rowTotal = 0.0;
                foreach (var phase in Phases)
                {
                    if (objTimings.TryGetValue(phase, out var pt) && pt.TotalSecs > 0)
                    {
                        cells.Add($"{FmtDur(pt.TotalSecs)} [dim]({pt.Count})[/]");
                        grandPhase[phase] += pt.TotalSecs;
                        rowTotal += pt.TotalSecs;
                    }
                    else
                    {
                        cells.Add("[dim]-[/]");
                    }
                }
                if (rowTotal > 0)
                {
                    var rowCells = new List<string> { $"[cyan]{Markup.Escape(name)}[/]" };
                    rowCells.AddRange(cells);
                    rowCells.Add($"[bold]{FmtDur(rowTotal)}[/]");
                    timingTbl.AddRow(rowCells.ToArray());
                }
            }

            // Totals row
            var grandTotalT = grandPhase.Values.Sum();
            if (grandTotalT > 0)
            {
                var totalCells = new List<string> { "[bold]Total[/]" };
                totalCells.AddRange(Phases.Select(p =>
                    grandPhase[p] > 0 ? $"[bold]{FmtDur(grandPhase[p])}[/]" : "[dim]-[/]"));
                totalCells.Add($"[bold]{FmtDur(grandTotalT)}[/]");
                timingTbl.AddRow(totalCells.ToArray());

                // Percentage row
                var pctCells = new List<string> { "[dim]% of time[/]" };
                pctCells.AddRange(Phases.Select(p =>
                    grandPhase[p] > 0 ? $"[dim]{Pct0(grandPhase[p] / grandTotalT)}[/]" : "[dim]-[/]"));
                pctCells.Add("[dim]100%[/]");
                timingTbl.AddRow(pctCells.ToArray());
            }
        }

        // -- Timing -----------------------------------------------------------
        var rateMin = overallRate * 60;
        var parts = new List<string> { $"  Elapsed: [bold]{FmtDur(elapsed)}[/]" };
        if (rateMin >= 1)
            parts.Add($"Rate: [bold]{rateMin.ToString("N0", inv)}/min[/]");
        else if (rateMin > 0)
            parts.Add($"Rate: [bold]{rateMin.ToString("F1", inv)}/min[/]");
        else
            parts.Add("Rate: [dim]--[/]");
        if (overallRate > 0 && grandTotal > totI)
            parts.Add($"ETA: [bold]~{FmtDur((grandTotal - totI) / overallRate)}[/]");
        var timing = new Markup(string.Join("  [dim]|[/]  ", parts));

        // -- Activity + error + footer (compact) --------------------------------
        var actDur = now - _activityT0;
        var activityEscaped = Markup.Escape(_activity);
        string actText;
        if (_activity == "Complete")
            actText = $"  [bold green]+ {activityEscaped}[/]";
        else if (_stopRequested)
            actText = $"  [bold yellow]! {activityEscaped}[/]";
        else if (_activity.Contains("Resolving ACLs") && _lastAclDuration > 0)
        {
            var remaining = Math.Max(0, _lastAclDuration - actDur);
            var aclEta = remaining > 0 ? $"  ETA ~{FmtDur(remaining)}" : "  (longer than usual)";
            actText = $"  [bold]> {activityEscaped}[/]  [dim]({FmtDur(actDur)}){aclEta}[/]";
        }
        else
            actText = $"  [bold]> {activityEscaped}[/]  [dim]({FmtDur(actDur)})[/]";

        var errText = "";
        if (!string.IsNullOrEmpty(_lastError))
        {
            // Truncate long errors to keep it on one line
            var err = _lastError.Length <= 90 ? _lastError : _lastError[..87] + "...";
            errText = $"\n  [red]Last error: {Markup.Escape(err)}[/]";
        }

        var hint = _stopRequested
            ? "  [bold yellow]Press Ctrl+X again to exit immediately[/]"
            : "  [dim]Ctrl+X = stop gracefully[/]";

        var bottom = new Markup($"{actText}{errText}\n{hint}");

        // -- Assemble ---------------------------------------------------------
        var elements = new List<IRenderable> { header, new Text(""), tbl, new Text(""), barGrid, new Text("") };
        if (timingTbl != null)
        {
            elements.Add(timingTbl);
            elements.Add(new Text(""));
        }
        elements.Add(timing);
        elements.Add(bottom);
        return new Rows(elements);
    }
}
