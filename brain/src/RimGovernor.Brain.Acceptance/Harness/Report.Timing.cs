using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// Every result.json carries the run's timing so a slower harness is visible without inferring runtimes from
// evidence mtimes: Create stamps started_at, Finalize adds finished_at, wall_ms, boot_ms (from the session open or
// game_reuse.openMs), ticks_advanced (the game ticks the harness observed pass, see TickStats) and wall_tps, and
// fails a passing run whose wall time exceeds its budget_ms.
public sealed partial class Report
{
    public const string StartedAtKey = "started_at";

    public const string FinishedAtKey = "finished_at";

    public const string WallMsKey = "wall_ms";

    public const string BootMsKey = "boot_ms";

    public const string BudgetMsKey = "budget_ms";

    public const string TicksAdvancedKey = "ticks_advanced";

    public const string WallTpsKey = "wall_tps";

    /// <summary>
    /// Records the report's budget (<c>budget_ms</c>): the wall-clock budget a passing run fails on when its wall time
    /// exceeds it, separate from the -timeout safety net. The case runner sets it from the case's Budget or its
    /// -budget override; a report without one is not held to any.
    /// </summary>
    public void SetBudget(TimeSpan budget) => this[BudgetMsKey] = (long)budget.TotalMilliseconds;

    /// <summary>
    /// The wall time at which the run's budget is spent (started_at + budget_ms): past it the run fails at Finalize
    /// whatever it does, so the runner cuts the case body there instead of letting a hung loop run on to the timeout
    /// safety net. Null without both fields.
    /// </summary>
    public DateTimeOffset? BudgetDeadline()
    {
        if (ParseReportTime(Data[StartedAtKey]) is not { } started || Data[BudgetMsKey].AsUInt64() is not { } ms || ms == 0)
        {
            return null;
        }

        return started.AddMilliseconds(ms);
    }

    /// <summary>Fills the timing fields at Finalize and applies the budget: a passing report over budget fails with <c>budget_exceeded</c>.</summary>
    internal void FinalizeTiming(DateTimeOffset finished)
    {
        this[FinishedAtKey] = finished.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        if (ParseReportTime(Data[StartedAtKey]) is not { } started)
        {
            return;
        }

        var wall = finished - started;
        this[WallMsKey] = (long)wall.TotalMilliseconds;
        if (!Has(BootMsKey) && Data["game_reuse"].AsMap() is { } reuse && reuse["openMs"].AsUInt64() is { } openMs)
        {
            this[BootMsKey] = (long)openMs;
        }

        var advanced = Data[TicksAdvancedKey].AsUInt64();
        if (advanced is null)
        {
            advanced = TickStats.Advanced;
            this[TicksAdvancedKey] = (long)advanced.Value;
        }

        if (!Has(WallTpsKey) && wall > TimeSpan.Zero)
        {
            this[WallTpsKey] = advanced.Value / wall.TotalSeconds;
        }

        var budget = Data[BudgetMsKey].AsUInt64() is { } budgetMs ? TimeSpan.FromMilliseconds(budgetMs) : TimeSpan.Zero;
        if (budget > TimeSpan.Zero && wall > budget)
        {
            this["budget_exceeded"] = true;
            if (Passed)
            {
                this["passed"] = false;
                this["error"] = $"run exceeded its budget: {TimeSpan.FromMilliseconds(Math.Round(wall.TotalMilliseconds))} > {budget}";
            }
        }
    }

    internal static DateTimeOffset? ParseReportTime(JsonNode? value)
    {
        var text = value.AsString();
        if (text.Length == 0)
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }
}

/// <summary>
/// Accumulates the game ticks the process has seen pass (Go <c>tickStats</c>): every harness reply that carries the
/// game tick (an identity read, the typed colony facts, the supervised clock) feeds <see cref="Observe"/>, which
/// sums the forward deltas between consecutive observations. A load (<see cref="ResetBaseline"/>) starts a new
/// baseline so a save's tick is not counted as progress, and a rewind only re-baselines.
/// </summary>
public static class TickStats
{
    private static readonly object Gate = new();
    private static bool _seen;
    private static ulong _last;
    private static ulong _advanced;

    /// <summary>The game ticks observed to pass so far.</summary>
    public static ulong Advanced
    {
        get
        {
            lock (Gate)
            {
                return _advanced;
            }
        }
    }

    public static void Observe(ulong tick)
    {
        lock (Gate)
        {
            if (_seen && tick > _last)
            {
                _advanced += tick - _last;
            }

            _seen = true;
            _last = tick;
        }
    }

    public static void ResetBaseline()
    {
        lock (Gate)
        {
            _seen = false;
        }
    }

    /// <summary>Empties the process's tick count so a runner that executes several cases in one process reports each case's own.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _seen = false;
            _last = 0;
            _advanced = 0;
        }
    }

    /// <summary>Feeds <see cref="Observe"/> with the tick a reply carried and re-baselines after a load or start.</summary>
    public static void ObserveReply(string tool, ulong? tick)
    {
        if (tool is "rimgovernor/load_game_ready" or "rimgovernor/lifecycle_load")
        {
            ResetBaseline();
            return;
        }

        if (tick is { } observed)
        {
            Observe(observed);
        }
    }

    /// <summary>
    /// The game tick a decoded reply carries, when it does: any <c>rimgovernor/*</c> ProtoJSON reply whose payload is a
    /// lifecycle <c>loaded</c> context (an identity read, a load, an authority acquisition) or an <c>observed</c>
    /// snapshot context (the typed colony facts).
    /// </summary>
    public static ulong? ReplyTick(string tool, JsonObject? payload)
    {
        if (!tool.StartsWith("rimgovernor/", StringComparison.Ordinal) || payload is null)
        {
            return null;
        }

        var encoded = payload["payload"];
        if (encoded is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        try
        {
            return WireTick(JsonNode.Parse(text) as JsonObject);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>A decoded reply's <c>loaded.context.tick</c> (lifecycle) or <c>observed.context.tick</c> (an observation snapshot).</summary>
    public static ulong? WireTick(JsonObject? message)
    {
        foreach (var key in new[] { "loaded", "observed" })
        {
            if (message.Member(key).AsMap() is { } outer && outer["context"].AsMap() is { } context)
            {
                return context["tick"].AsUInt64();
            }
        }

        return null;
    }
}
