using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What a report carries under <c>wait_stats</c> (Go <c>waitStats</c>): how many waits the run made, the longest a
/// signature stayed unchanged before it moved or the wait ended (the evidence for tuning
/// <see cref="Wait.DefaultStall"/>), and how many waits stalled.
/// </summary>
public static class WaitStatistics
{
    private static readonly Lock Gate = new();
    private static int s_waits;
    private static int s_stalled;
    private static TimeSpan s_maxQuiet;
    private static string s_maxLabel = "";

    /// <summary>Folds one finished wait into the statistics.</summary>
    internal static void Record(TimeSpan quiet, string signature, bool stalled)
    {
        lock (Gate)
        {
            s_waits++;
            if (stalled)
            {
                s_stalled++;
            }

            if (quiet > s_maxQuiet)
            {
                s_maxQuiet = quiet;
                s_maxLabel = signature;
            }
        }
    }

    /// <summary>Empties the process's wait statistics so a runner that executes several cases in one process reports each case's own.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            s_waits = 0;
            s_stalled = 0;
            s_maxQuiet = TimeSpan.Zero;
            s_maxLabel = "";
        }
    }

    /// <summary>The run's wait statistics for the report.</summary>
    public static JsonObject Snapshot()
    {
        lock (Gate)
        {
            return new JsonObject
            {
                ["waits"] = s_waits,
                ["stalled"] = s_stalled,
                ["max_quiet_ms"] = (long)s_maxQuiet.TotalMilliseconds,
                ["max_quiet_signature"] = s_maxLabel,
                ["stall_budget_ms"] = (long)Wait.StallBudget().TotalMilliseconds,
            };
        }
    }
}
