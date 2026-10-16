using System.Diagnostics;

namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// One buffered launch phase: a UTC interval, its id and the id of the phase it nests in (0 for a direct child of
/// the root). A parent's id is lower than its children's (ids are taken when a phase starts). <paramref name="Attributes"/>
/// become the span's tags (the harness and launcher name their <c>rimgovernor.launch.source</c>).
/// </summary>
public sealed record LaunchInterval(int Id, int Parent, string Name, DateTime StartUtc, DateTime EndUtc, IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>
/// Replays a launch waterfall as spans: one root <c>launch</c> from the earliest phase to <paramref name="endUtc"/>,
/// each phase a child of its parent phase or of the root, with the recorded start and end times. Component
/// <c>launch</c>. The game-side waterfall (#3286) is replayed once the brain's span listener exists, ending when the
/// brain started; the harness's and launcher's phases (#3287) arrive over <c>POST /api/telemetry/spans</c> and are
/// replayed as their own <c>launch</c> tree, ending with their last phase.
/// </summary>
public static class LaunchSpans
{
    public const string Component = "launch";
    public const string Root = "launch";

    private static readonly Dictionary<string, string> EmptyAttributes = [];

    /// <summary>Emits the spans and returns how many were written (0 when nothing listens or there is no phase).</summary>
    public static int Replay(IReadOnlyList<LaunchInterval> phases, DateTime endUtc)
    {
        ArgumentNullException.ThrowIfNull(phases);
        if (phases.Count == 0)
        {
            return 0;
        }
        var source = Spans.Source(Component);
        var previous = Activity.Current;
        var started = new List<Activity>(phases.Count + 1);
        try
        {
            Activity.Current = null;
            var rootStart = phases.Min(p => p.StartUtc);
            var root = source.StartActivity(Root, ActivityKind.Internal, default(ActivityContext), startTime: rootStart);
            if (root is null)
            {
                return 0;
            }
            started.Add(root);
            var byId = new Dictionary<int, Activity>();
            foreach (var phase in phases.OrderBy(p => p.Id))
            {
                // A parent that was dropped or never finished leaves its children under the root.
                var parent = byId.TryGetValue(phase.Parent, out var p) ? p.Context : root.Context;
                var span = source.StartActivity(phase.Name, ActivityKind.Internal, parent, startTime: phase.StartUtc);
                if (span is null)
                {
                    continue;
                }
                foreach (var (key, value) in phase.Attributes ?? EmptyAttributes)
                {
                    span.SetTag(key, value);
                }
                span.SetEndTime(End(phase.StartUtc, phase.EndUtc));
                byId[phase.Id] = span;
                started.Add(span);
            }
            root.SetEndTime(End(rootStart, endUtc));
        }
        finally
        {
            // Children first, so a reader that sees a parent has already seen everything under it.
            for (var i = started.Count - 1; i >= 0; i--)
            {
                started[i].Stop();
            }
            Activity.Current = previous;
        }
        return started.Count;
    }

    // A zero duration would make Activity.Stop stamp the replay time as the end, so a span lasts at least one tick.
    private static DateTime End(DateTime start, DateTime end) => end > start ? end : start.AddTicks(1);
}
