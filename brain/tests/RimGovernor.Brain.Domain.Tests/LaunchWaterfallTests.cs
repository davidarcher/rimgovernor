using System.Globalization;
using System.Text;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Telemetry;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// <c>RimGovernor.cmd launch-phases</c> (#3288): a fixture <c>traces.jsonl</c> holding an older run's boot, the latest
/// run's game tree (with a nested parallel RimWorld stage) and the harness's posted tree renders to the expected
/// waterfall lines and JSON, and two runs render to the per-phase delta.
/// </summary>
public sealed class LaunchWaterfallTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static readonly DateTime Origin = new(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RendersTheLatestRunAsAWaterfall()
    {
        var fall = LaunchWaterfall.LatestRun(LaunchWaterfall.ReadSpans(Fixture(RunA)));

        var name = 27;
        string Row(long offset, long dur, string label, int from, int to) =>
            string.Create(CultureInfo.InvariantCulture, $"{offset,9} {dur,8}  {label.PadRight(name)}  |{new string(' ', from)}{new string('#', to - from)}{new string(' ', 40 - to)}|");
        Assert.Equal(
            [
                $"{"offset_ms",9} {"dur_ms",8}  {"phase".PadRight(name)}  waterfall",
                Row(-2000, 14000, "launch [harness]", 0, 40),
                Row(-2000, 500, "  launch.prepare", 0, 2),
                Row(-1500, 500, "  launch.process_start", 1, 3),
                Row(-1000, 12000, "  launch.connect", 2, 38),
                Row(11000, 1000, "  start.load", 37, 40),
                Row(0, 10000, "launch [game]", 5, 35),
                Row(0, 1000, "  launch.pre_mods", 5, 9),
                Row(1000, 3000, "  rimworld/LoadModXML()", 8, 18),
                Row(1000, 2000, "    rimworld/a", 8, 15),
                Row(1500, 1000, "    rimworld/b", 10, 13),
                Row(6000, 2000, "  bridge-start.total", 22, 29),
                Row(6000, 1500, "    capabilities.initialize", 22, 28),
                Row(9000, 1000, "  brain.host_start", 31, 35),
                "total 14000 ms",
                "largest: launch.connect 12000 ms, rimworld/LoadModXML() 3000 ms, bridge-start.total 2000 ms",
            ],
            fall.Lines());

        var json = fall.ToJson();
        Assert.Equal("runA", json["run_id"]!.GetValue<string>());
        Assert.Equal(14000, json["total_ms"]!.GetValue<long>());
        var spans = json["spans"]!.AsArray();
        Assert.Equal(13, spans.Count);
        Assert.Equal(("launch", "harness", 0, -2000L), (spans[0]!["name"]!.GetValue<string>(), spans[0]!["source"]!.GetValue<string>(), spans[0]!["depth"]!.GetValue<int>(), spans[0]!["offset_ms"]!.GetValue<long>()));
        var parallel = spans[9]!;
        Assert.Equal(("rimworld/b", LaunchWaterfall.GameSource, 2, 1500L, 1000L), (parallel["name"]!.GetValue<string>(), parallel["source"]!.GetValue<string>(), parallel["depth"]!.GetValue<int>(), parallel["offset_ms"]!.GetValue<long>(), parallel["duration_ms"]!.GetValue<long>()));
        Assert.Equal(["launch.connect", "rimworld/LoadModXML()", "bridge-start.total"], json["largest"]!.AsArray().Select(l => l!["name"]!.GetValue<string>()));
    }

    [Fact]
    public void AReportWindowKeepsOnlyTheTreesRootedInIt()
    {
        var spans = LaunchWaterfall.ReadSpans(Fixture(RunA));
        var fall = LaunchWaterfall.Since(spans, new DateTimeOffset(Origin), new DateTimeOffset(Origin.AddSeconds(1)));
        Assert.Equal(8, fall.Spans.Count);
        Assert.All(fall.Spans, s => Assert.Equal(LaunchWaterfall.GameSource, s.Source));
        Assert.Equal(10000, fall.TotalMs);
        Assert.Empty(LaunchWaterfall.Since(spans, new DateTimeOffset(Origin.AddHours(1))).Spans);
    }

    [Fact]
    public void TwoRunsRenderThePerPhaseDelta()
    {
        var before = LaunchWaterfall.LatestRun(LaunchWaterfall.ReadSpans(Fixture(RunA)));
        var after = LaunchWaterfall.LatestRun(LaunchWaterfall.ReadSpans(Fixture(RunB)));

        var name = 23;
        string Row(string phase, long a, long b, string delta) =>
            string.Create(CultureInfo.InvariantCulture, $"{phase.PadRight(name)}  {a,9} {b,9} {delta,9}");
        var lines = LaunchWaterfall.DeltaLines(before, after);
        Assert.Equal(
            [
                $"{"phase".PadRight(name)}  {"before_ms",9} {"after_ms",9} {"delta_ms",9}",
                Row("total", 14000, 12000, "-2000"),
                Row("launch.connect", 12000, 10000, "-2000"),
                Row("rimworld/LoadModXML()", 3000, 2000, "-1000"),
                Row("rimworld/a", 2000, 1500, "-500"),
                Row("brain.host_start", 1000, 1000, "0"),
            ],
            lines.Take(6));
        Assert.Equal(13, lines.Count);

        var json = LaunchWaterfall.DeltaJson(before, after);
        Assert.Equal("runB", json["after"]!["run_id"]!.GetValue<string>());
        var connect = json["phases"]!.AsArray()[1]!;
        Assert.Equal(("launch.connect", 12000L, 10000L, -2000L), (connect["name"]!.GetValue<string>(), connect["before_ms"]!.GetValue<long>(), connect["after_ms"]!.GetValue<long>(), connect["delta_ms"]!.GetValue<long>()));
    }

    // (run, trace, span, parent, name, start ms, end ms, source) relative to Origin; parent "" is a root.
    private static readonly (string Run, string Trace, string Span, string Parent, string Name, int Start, int End, string Source)[] RunA =
    [
        // An older game's boot in the same ring: not the latest run.
        ("old", "g0", "r0", "", "launch", -3_600_000, -3_590_000, ""),
        ("old", "g0", "p0", "r0", "launch.pre_mods", -3_600_000, -3_599_000, ""),
        // The game's own tree, written children first as the replay stops them.
        ("runA", "g1", "a1", "x1", "rimworld/a", 1000, 3000, ""),
        ("runA", "g1", "b1", "x1", "rimworld/b", 1500, 2500, ""),
        ("runA", "g1", "x1", "r1", "rimworld/LoadModXML()", 1000, 4000, ""),
        ("runA", "g1", "c1", "s1", "capabilities.initialize", 6000, 7500, ""),
        ("runA", "g1", "s1", "r1", "bridge-start.total", 6000, 8000, ""),
        ("runA", "g1", "h1", "r1", "brain.host_start", 9000, 10000, ""),
        ("runA", "g1", "m1", "r1", "launch.pre_mods", 0, 1000, ""),
        ("runA", "g1", "r1", "", "launch", 0, 10000, ""),
        // The harness's post, replayed later as its own tree.
        ("runA", "t1", "pp", "q1", "launch.prepare", -2000, -1500, "harness"),
        ("runA", "t1", "ps", "q1", "launch.process_start", -1500, -1000, "harness"),
        ("runA", "t1", "pc", "q1", "launch.connect", -1000, 11000, "harness"),
        ("runA", "t1", "pl", "q1", "start.load", 11000, 12000, "harness"),
        ("runA", "t1", "q1", "", "launch", -2000, 12000, ""),
    ];

    private static readonly (string Run, string Trace, string Span, string Parent, string Name, int Start, int End, string Source)[] RunB =
    [
        ("runB", "g2", "a2", "x2", "rimworld/a", 1000, 2500, ""),
        ("runB", "g2", "b2", "x2", "rimworld/b", 1500, 2500, ""),
        ("runB", "g2", "x2", "r2", "rimworld/LoadModXML()", 1000, 3000, ""),
        ("runB", "g2", "c2", "s2", "capabilities.initialize", 6000, 7500, ""),
        ("runB", "g2", "s2", "r2", "bridge-start.total", 6000, 8000, ""),
        ("runB", "g2", "h2", "r2", "brain.host_start", 9000, 10000, ""),
        ("runB", "g2", "m2", "r2", "launch.pre_mods", 0, 1000, ""),
        ("runB", "g2", "r2", "", "launch", 0, 10000, ""),
        ("runB", "t2", "pp", "q2", "launch.prepare", -2000, -1500, "harness"),
        ("runB", "t2", "ps", "q2", "launch.process_start", -1500, -1000, "harness"),
        ("runB", "t2", "pc", "q2", "launch.connect", -1000, 9000, "harness"),
        ("runB", "t2", "pl", "q2", "start.load", 9000, 10000, "harness"),
        ("runB", "t2", "q2", "", "launch", -2000, 10000, ""),
    ];

    // Writes the spans as an OTLP/JSON traces ring, one span per line under the launch scope, and returns its path.
    private string Fixture((string Run, string Trace, string Span, string Parent, string Name, int Start, int End, string Source)[] spans)
    {
        var directory = Path.Combine(Path.GetTempPath(), "launch-waterfall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        var b = new StringBuilder();
        foreach (var s in spans)
        {
            var attrs = s.Source.Length == 0 ? "" : $$$""","attributes":[{"key":"{{{LaunchTrace.SourceAttribute}}}","value":{"stringValue":"{{{s.Source}}}"}}]""";
            var parent = s.Parent.Length == 0 ? "" : ",\"parentSpanId\":\"" + s.Parent + "\"";
            b.Append(CultureInfo.InvariantCulture, $$$"""{"resourceSpans":[{"resource":{"attributes":[{"key":"service.instance.id","value":{"stringValue":"{{{s.Run}}}"}}]},"scopeSpans":[{"scope":{"name":"{{{Spans.ScopePrefix + LaunchSpans.Component}}}"},"spans":[{"traceId":"{{{s.Trace}}}","spanId":"{{{s.Span}}}"{{{parent}}},"name":"{{{s.Name}}}","kind":1,"startTimeUnixNano":"{{{Nanos(s.Start)}}}","endTimeUnixNano":"{{{Nanos(s.End)}}}"{{{attrs}}}}]}]}]}""").Append('\n');
        }

        var path = Path.Combine(directory, "traces.jsonl");
        File.WriteAllText(path, b.ToString());
        return path;
    }

    private static long Nanos(int ms) => (Origin.AddMilliseconds(ms).Ticks - DateTime.UnixEpoch.Ticks) * 100;
}
