using System.Diagnostics;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Laps one step into named stages: the wall time and the process allocation between consecutive
/// <see cref="Lap"/> calls, published on the step's row as <c>stage_ms</c> and <c>stage_alloc_kb</c> (the #3556
/// step profile). Allocation is the process total, so a stage that overlaps parallel planners counts their
/// allocation too; the game thread's facade work runs in the same process and counts as well.
/// </summary>
public sealed class StepStages
{
    private readonly Dictionary<string, double> _ms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _allocKb = new(StringComparer.Ordinal);
    private long _at = Stopwatch.GetTimestamp();
    private long _bytes = GC.GetTotalAllocatedBytes();

    /// <summary>Ends the stage that began at the previous lap under <paramref name="name"/>; a repeated name accumulates.</summary>
    public void Lap(string name)
    {
        var now = Stopwatch.GetTimestamp();
        var bytes = GC.GetTotalAllocatedBytes();
        _ms[name] = _ms.GetValueOrDefault(name) + Stopwatch.GetElapsedTime(_at, now).TotalMilliseconds;
        _allocKb[name] = _allocKb.GetValueOrDefault(name) + (bytes - _bytes) / 1024.0;
        (_at, _bytes) = (now, bytes);
    }

    /// <summary>Each lapped stage's wall time in milliseconds.</summary>
    public IReadOnlyDictionary<string, double> Ms => _ms;

    /// <summary>Writes <c>stage_ms</c> and <c>stage_alloc_kb</c> onto <paramref name="attrs"/> when any stage was lapped.</summary>
    public void Publish(Dictionary<string, object?> attrs)
    {
        if (_ms.Count == 0)
        {
            return;
        }
        var ms = new Dictionary<string, object?>(StringComparer.Ordinal);
        var alloc = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in _ms)
        {
            ms[name] = value;
            alloc[name] = _allocKb[name];
        }
        attrs["stage_ms"] = ms;
        attrs["stage_alloc_kb"] = alloc;
    }
}
