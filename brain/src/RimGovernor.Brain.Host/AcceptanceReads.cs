using RimGovernor.Brain.BuildingRuntime;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The game-side measurement the out-of-process acceptance harness cannot make itself: the brain's decode of one captured
/// frame, timed for the mod's read-only capture profile probe. Nothing here writes.
/// </summary>
public static class AcceptanceReads
{
    /// <summary>
    /// Measures the brain's reads of one captured frame for the capture profile probe: the held tables and the
    /// whole-map planning window over the frame's grid, each <paramref name="iterations"/> times on the calling thread.
    /// Returns (span, ms, allocated bytes) samples; nothing is kept.
    /// </summary>
    public static IReadOnlyList<(string Span, double Ms, long Bytes)> FrameDecodeProfile(RimGovernor.Contracts.Wire.Observations.BundleSnapshot frame, int iterations)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var samples = new List<(string, double, long)>();
        T Time<T>(string span, Func<T> body)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = body();
            samples.Add((span, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes));
            return result;
        }
        for (var i = 0; i < iterations; i++)
        {
            if (frame.Context is { } context)
            {
                Time("brain.heldTables", () => FrameHeld.Of(frame, context));
            }
            if (frame.Grid is { } wire)
            {
                var grid = new FrameCellGrid(wire);
                Time("brain.windowWholeMap", () => grid.Window(grid.Rect));
            }
        }
        return samples;
    }
}
