using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The constants, window and serve spec the sustained cases share, and the <c>sustained/food</c> diagnostic that
/// records EnsureFoodSupply timelines for the baseline colony and its save variants (Go <c>sustained/food.go</c>).
/// Diagnostic cases fail on harness errors, not poor food outcomes, so unfavorable outcomes remain visible in
/// reports. <c>sustained/winter</c> separately asserts seasonal reserve survival across the first non-growing day.
/// </summary>
internal static class SustainedFoodCase
{
    /// <summary>The hand-prepared eight-colonist tribal start every serve-driven case loads unless it names another save.</summary>
    internal const string BaselineSave = "RimGovernor-tribal8-baseline";

    /// <summary>The sample window: wall-clock minutes at the serve clock speed, not ticks; the regression-gate length.</summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(8);

    /// <summary>The environment variable that overrides <see cref="DefaultWindow"/> with a Go duration (e.g. 20m).</summary>
    internal const string WindowEnv = "RIMGOVERNOR_ACCEPT_WINDOW";

    /// <summary>
    /// The matrix variants' sample window in game ticks (one in-game hour): a variant's watch ends once the live tick
    /// has advanced this far, and <see cref="Window"/> only caps a game that stops advancing.
    /// </summary>
    internal const ulong MatrixWindowTicks = 2500;

    /// <summary><see cref="WindowEnv"/> when set and valid, else <see cref="DefaultWindow"/>.</summary>
    internal static TimeSpan Window()
    {
        var raw = Environment.GetEnvironmentVariable(WindowEnv);
        if (!string.IsNullOrEmpty(raw) && GoDuration.TryParse(raw, out var d) && d > TimeSpan.Zero)
        {
            return d;
        }

        return DefaultWindow;
    }

    /// <summary>
    /// The serve spec every food-pipeline observation launches: EnsureFoodSupply's own families only (field growing,
    /// food storage, harvest/wood acquisition, cooking bills, starting supplies), so the food outcome under diagnosis
    /// is not confounded by other families and every family shares one step budget. StepStall fails fast when no
    /// scheduler step runs.
    /// </summary>
    internal static ServeSpec Spec(string prefix) => new()
    {
        Families = ["field", "stockpiles", "acquisition", "cooking", "supply"],
        NativeTimeout = TimeSpan.FromSeconds(15),
        StepStall = TimeSpan.FromSeconds(90),
        Prefix = prefix,
    };

    /// <summary>The Go <c>errors.Join</c> of the failures that are not null; null when none failed.</summary>
    internal static AcceptanceException? Join(IEnumerable<Exception?> failures)
    {
        var messages = failures.Where(f => f is not null).Select(f => f!.Message).ToList();
        return messages.Count == 0 ? null : new AcceptanceException(string.Join('\n', messages));
    }

    /// <summary>Runs <paramref name="step"/> and returns its harness failure, null when it passed.</summary>
    internal static async Task<Exception?> CaptureAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            return null;
        }
        catch (AcceptanceException e)
        {
            return e;
        }
    }

    /// <summary>The samples of the report's timeline, in sample order.</summary>
    internal static List<JsonObject> TimelineOf(Report report)
    {
        var timeline = new List<JsonObject>();
        foreach (var raw in report["timeline"].AsSlice())
        {
            if (raw.AsMap() is { } sample)
            {
                timeline.Add(sample);
            }
        }

        return timeline;
    }

    /// <summary>The report's tick-window block (<c>report["window"]</c>), null when the watch never recorded one.</summary>
    internal static JsonObject? WindowOf(Report report) => report["window"].AsMap();

    internal static AcceptanceCase Case(string name, string save, string prefix, ulong ticks) => new()
    {
        Name = name,
        Scope = "Diagnostic: EnsureFoodSupply standard-state timeline against the " + save +
            " save under the live rounder/field planner, evidence for issue #1's eight-colonist crop labor / interim food deficit. Not a pass/fail acceptance gate.",
        Start = new SaveStart { Name = save },
        Keep = [NeedDef.Food.Value],
        Serve = Spec(prefix),

        // Load, acquire, stop and reattach fit in the margin over the window.
        Budget = Window() + TimeSpan.FromMinutes(7),
        Crew = new Crew { Size = 3 },
        Run = async (s, cancellationToken) =>
        {
            _ = await SustainedFood.ObserveAsync(
                ObservedSession.Of(s),
                new SustainedObservation { Watch = new WatchConfig { Watch = Window(), Window = ticks } },
                cancellationToken).ConfigureAwait(false);
        },
    };
}
