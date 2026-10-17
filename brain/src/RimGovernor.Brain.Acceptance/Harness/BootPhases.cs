using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The result.json <c>boot_phases</c> map (#3287): every launch phase of this run, name → milliseconds, read from the
/// game's <c>traces.jsonl</c>. It covers each <c>launch</c> tree whose root starts at or after the run started: the
/// game's own waterfall when this run booted the game (a kept game booted earlier contributes none) and the
/// harness's posted phases (<see cref="LaunchTrace"/>), selected by <see cref="LaunchWaterfall.Since"/>. Roots are not
/// phases. A name seen more than once sums its durations. Phases overlap (a stage nests in another, the harness's connect spans the game's load), so the values
/// do not add up to <c>boot_ms</c>.
/// </summary>
public static class BootPhases
{
    public const string Key = "boot_phases";

    /// <summary>The phases of the launch trees in the traces ring at <paramref name="tracesPath"/> rooted at or after <paramref name="since"/>; empty when there are none.</summary>
    public static JsonObject Read(string tracesPath, DateTimeOffset since)
    {
        var phases = LaunchWaterfall.Since(LaunchWaterfall.ReadSpans(tracesPath), since).Phases();
        var map = new JsonObject();
        foreach (var (name, ms) in phases)
        {
            map.Set(name, (long)Math.Round(ms));
        }

        return map;
    }
}
