using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One line of a <see cref="LaunchWaterfall"/>: a span, its tree's source, its depth (0 for a <c>launch</c> root) and its offset and duration in ms.</summary>
public sealed record WaterfallSpan(string Name, string Source, int Depth, double OffsetMs, double DurationMs);

/// <summary>
/// The launch waterfall of one run (#3288, <c>RimGovernor.cmd launch-phases</c>): the <c>launch</c> trees of a game's
/// <c>traces.jsonl</c> (see otel-signals.md "Launch spans"), ordered by root start, each tree depth first with
/// siblings in start order. Offsets count from the game's own <c>launch</c> root (process start, which
/// <c>launch.pre_mods</c> begins at), so the harness's and launcher's earlier phases are negative; without a game tree
/// they count from the earliest root. <see cref="BootPhases"/> reads the same trees.
/// </summary>
public sealed class LaunchWaterfall
{
    /// <summary>The source of a tree whose spans carry no <see cref="LaunchTrace.SourceAttribute"/>: the game's own boot.</summary>
    public const string GameSource = "game";

    private const int BarWidth = 40;
    private const int NameWidthCap = 56;

    private LaunchWaterfall(string runId, IReadOnlyList<WaterfallSpan> spans)
    {
        RunId = runId;
        Spans = spans;
    }

    /// <summary>The run id (<c>service.instance.id</c>) of the brain that wrote the trees; empty when there are none.</summary>
    public string RunId { get; }

    public IReadOnlyList<WaterfallSpan> Spans { get; }

    /// <summary>The earliest start to the latest end, in ms.</summary>
    public double TotalMs => Spans.Count == 0 ? 0 : Spans.Max(s => s.OffsetMs + s.DurationMs) - Spans.Min(s => s.OffsetMs);

    /// <summary>Every <c>launch</c>-scope span of the traces ring at <paramref name="tracesPath"/>, oldest first.</summary>
    public static List<OtlpSpan> ReadSpans(string tracesPath)
    {
        var spans = new List<OtlpSpan>();
        OtlpReader.ReadSpans(tracesPath, LaunchSpans.Component, spans.Add);
        return spans;
    }

    /// <summary>
    /// The trees whose root starts in [<paramref name="since"/>, <paramref name="until"/>] (open-ended without
    /// <paramref name="until"/>): an acceptance report's run, so a kept game's earlier boot is not counted.
    /// </summary>
    public static LaunchWaterfall Since(IReadOnlyList<OtlpSpan> spans, DateTimeOffset since, DateTimeOffset? until = null)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var from = Nanos(since);
        var to = until is { } u ? Nanos(u) : long.MaxValue;
        return Build(spans, Roots(spans).Where(r => r.StartUnixNano >= from && r.StartUnixNano <= to));
    }

    /// <summary>Every tree of the run id of the latest root: one game process, its own boot and each harness or launcher post into it.</summary>
    public static LaunchWaterfall LatestRun(IReadOnlyList<OtlpSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var roots = Roots(spans).ToList();
        if (roots.Count == 0)
        {
            return new LaunchWaterfall("", []);
        }

        var run = roots.MaxBy(r => r.StartUnixNano)!.Resource.Instance;
        return Build(spans, roots.Where(r => r.Resource.Instance == run));
    }

    /// <summary>Phase name → summed ms over the non-root spans; a name seen more than once sums.</summary>
    public SortedDictionary<string, double> Phases()
    {
        var phases = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var span in Spans.Where(s => s.Depth > 0))
        {
            phases[span.Name] = phases.GetValueOrDefault(span.Name) + span.DurationMs;
        }

        return phases;
    }

    /// <summary>The <paramref name="count"/> longest non-root spans, longest first.</summary>
    public IReadOnlyList<WaterfallSpan> Largest(int count = 3) =>
        [.. Spans.Where(s => s.Depth > 0).OrderByDescending(s => s.DurationMs).ThenBy(s => s.Name, StringComparer.Ordinal).Take(count)];

    /// <summary>The text waterfall: a header, one line per span (offset, duration, indented name, bar), then the total and the largest phases.</summary>
    public IReadOnlyList<string> Lines()
    {
        if (Spans.Count == 0)
        {
            return ["no launch spans"];
        }

        var labels = Spans.Select(Label).ToList();
        var nameWidth = Math.Min(NameWidthCap, Math.Max("phase".Length, labels.Max(l => l.Length)));
        var min = Spans.Min(s => s.OffsetMs);
        var range = Math.Max(TotalMs, 1);
        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{"offset_ms",9} {"dur_ms",8}  {Pad("phase", nameWidth)}  waterfall") };
        for (var i = 0; i < Spans.Count; i++)
        {
            var s = Spans[i];
            var from = (int)Math.Floor((s.OffsetMs - min) / range * BarWidth);
            var to = (int)Math.Ceiling((s.OffsetMs + s.DurationMs - min) / range * BarWidth);
            from = Math.Clamp(from, 0, BarWidth - 1);
            to = Math.Clamp(Math.Max(to, from + 1), 1, BarWidth);
            var bar = new string(' ', from) + new string('#', to - from) + new string(' ', BarWidth - to);
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{Ms(s.OffsetMs),9} {Ms(s.DurationMs),8}  {Pad(labels[i], nameWidth)}  |{bar}|"));
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture, $"total {Ms(TotalMs)} ms"));
        lines.Add("largest: " + string.Join(", ", Largest().Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Name} {Ms(s.DurationMs)} ms"))));
        return lines;
    }

    /// <summary>The waterfall as JSON: run_id, total_ms, spans (name, source, depth, offset_ms, duration_ms) and largest.</summary>
    public JsonObject ToJson()
    {
        var spans = new JsonArray();
        foreach (var s in Spans)
        {
            spans.Add(new JsonObject
            {
                { "name", s.Name },
                { "source", s.Source },
                { "depth", s.Depth },
                { "offset_ms", Ms(s.OffsetMs) },
                { "duration_ms", Ms(s.DurationMs) },
            });
        }

        var largest = new JsonArray();
        foreach (var s in Largest())
        {
            largest.Add(new JsonObject { { "name", s.Name }, { "duration_ms", Ms(s.DurationMs) } });
        }

        return new JsonObject { { "run_id", RunId }, { "total_ms", Ms(TotalMs) }, { "spans", spans }, { "largest", largest } };
    }

    /// <summary>The per-phase delta of <paramref name="after"/> against <paramref name="before"/>: the total first, then each phase by largest change.</summary>
    public static IReadOnlyList<(string Name, long BeforeMs, long AfterMs)> Delta(LaunchWaterfall before, LaunchWaterfall after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var a = before.Phases();
        var b = after.Phases();
        var rows = a.Keys.Union(b.Keys, StringComparer.Ordinal)
            .Select(name => (Name: name, BeforeMs: Ms(a.GetValueOrDefault(name)), AfterMs: Ms(b.GetValueOrDefault(name))))
            .OrderByDescending(r => Math.Abs(r.AfterMs - r.BeforeMs))
            .ThenBy(r => r.Name, StringComparer.Ordinal);
        return [("total", Ms(before.TotalMs), Ms(after.TotalMs)), .. rows];
    }

    /// <summary>The delta as text: one line per phase with both durations and the change.</summary>
    public static IReadOnlyList<string> DeltaLines(LaunchWaterfall before, LaunchWaterfall after)
    {
        var rows = Delta(before, after);
        var nameWidth = Math.Min(NameWidthCap, rows.Max(r => r.Name.Length));
        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{Pad("phase", nameWidth)}  {"before_ms",9} {"after_ms",9} {"delta_ms",9}") };
        foreach (var (name, beforeMs, afterMs) in rows)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{Pad(name, nameWidth)}  {beforeMs,9} {afterMs,9} {afterMs - beforeMs,9:+0;-0;0}"));
        }

        return lines;
    }

    /// <summary>The delta as JSON: before and after (each waterfall's JSON) and phases (name, before_ms, after_ms, delta_ms).</summary>
    public static JsonObject DeltaJson(LaunchWaterfall before, LaunchWaterfall after)
    {
        var phases = new JsonArray();
        foreach (var (name, beforeMs, afterMs) in Delta(before, after))
        {
            phases.Add(new JsonObject { { "name", name }, { "before_ms", beforeMs }, { "after_ms", afterMs }, { "delta_ms", afterMs - beforeMs } });
        }

        return new JsonObject { { "before", before.ToJson() }, { "after", after.ToJson() }, { "phases", phases } };
    }

    private static IEnumerable<OtlpSpan> Roots(IEnumerable<OtlpSpan> spans) =>
        spans.Where(s => s.ParentSpanId.Length == 0 && s.Name == LaunchSpans.Root);

    private static LaunchWaterfall Build(IReadOnlyList<OtlpSpan> spans, IEnumerable<OtlpSpan> roots)
    {
        var chosen = roots.OrderBy(r => r.StartUnixNano).ToList();
        if (chosen.Count == 0)
        {
            return new LaunchWaterfall("", []);
        }

        var traces = chosen.Select(r => r.TraceId).ToHashSet(StringComparer.Ordinal);
        var children = spans
            .Where(s => s.ParentSpanId.Length > 0 && traces.Contains(s.TraceId))
            .ToLookup(s => (s.TraceId, s.ParentSpanId));
        var trees = spans.Where(s => traces.Contains(s.TraceId)).ToLookup(s => s.TraceId, StringComparer.Ordinal);
        var sources = chosen.ToDictionary(r => r.TraceId, r => Source(trees[r.TraceId]), StringComparer.Ordinal);
        var zero = chosen.FirstOrDefault(r => sources[r.TraceId] == GameSource)?.StartUnixNano ?? chosen[0].StartUnixNano;

        var lines = new List<WaterfallSpan>();
        foreach (var root in chosen)
        {
            Walk(root, 0, sources[root.TraceId]);
        }

        return new LaunchWaterfall(chosen[^1].Resource.Instance, lines);

        void Walk(OtlpSpan span, int depth, string source)
        {
            lines.Add(new WaterfallSpan(span.Name, source, depth, (span.StartUnixNano - zero) / 1e6, span.Duration.TotalMilliseconds));
            foreach (var child in children[(span.TraceId, span.SpanId)]
                .OrderBy(c => c.StartUnixNano)
                .ThenByDescending(c => c.EndUnixNano)
                .ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                Walk(child, depth + 1, source);
            }
        }
    }

    // A posted tree's spans name their source; the game's own tree names none.
    private static string Source(IEnumerable<OtlpSpan> tree) =>
        tree.Select(s => s.Attrs.Str(LaunchTrace.SourceAttribute)).FirstOrDefault(s => s.Length > 0) ?? GameSource;

    private static string Label(WaterfallSpan s) =>
        new string(' ', 2 * s.Depth) + (s.Depth == 0 ? $"{s.Name} [{s.Source}]" : s.Name);

    private static string Pad(string text, int width) =>
        text.Length > width ? text[..(width - 1)] + "~" : text.PadRight(width);

    private static long Ms(double ms) => (long)Math.Round(ms, MidpointRounding.AwayFromZero);

    private static long Nanos(DateTimeOffset at) => (at.UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
}
