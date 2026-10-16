using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The result.json <c>boot_phases</c> map (#3287): every launch phase of this run, name → milliseconds, read from the
/// game's <c>traces.jsonl</c>. It covers each <c>launch</c> tree whose root starts at or after the run started: the
/// game's own waterfall when this run booted the game (a kept game booted earlier contributes none) and the
/// harness's posted phases (<see cref="LaunchTrace"/>). Roots are not phases. A name seen more than once sums its
/// durations. Phases overlap (a stage nests in another, the harness's connect spans the game's load), so the values
/// do not add up to <c>boot_ms</c>.
/// </summary>
public static class BootPhases
{
    public const string Key = "boot_phases";

    /// <summary>The phases of the launch trees in the traces ring at <paramref name="tracesPath"/> rooted at or after <paramref name="since"/>; empty when there are none.</summary>
    public static JsonObject Read(string tracesPath, DateTimeOffset since)
    {
        var spans = new List<OtlpSpan>();
        OtlpReader.ReadSpans(tracesPath, LaunchSpans.Component, spans.Add);
        var sinceNanos = (since.UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
        var traces = spans
            .Where(s => s.ParentSpanId.Length == 0 && s.Name == LaunchSpans.Root && s.StartUnixNano >= sinceNanos)
            .Select(s => s.TraceId)
            .ToHashSet(StringComparer.Ordinal);
        var phases = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var span in spans.Where(s => s.ParentSpanId.Length > 0 && traces.Contains(s.TraceId)))
        {
            phases[span.Name] = phases.GetValueOrDefault(span.Name) + span.Duration.TotalMilliseconds;
        }

        var map = new JsonObject();
        foreach (var (name, ms) in phases)
        {
            map[name] = (long)Math.Round(ms);
        }

        return map;
    }
}
