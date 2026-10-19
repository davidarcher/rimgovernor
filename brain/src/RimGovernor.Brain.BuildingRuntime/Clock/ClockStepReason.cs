using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Why the scheduler stepped. Every step starts from a held clock or a step that did not end in a running one; there is no wall-clock cadence (#3556).</summary>
public enum StepCause
{
    /// <summary>
    /// The review tick the last Run named was reached and native held the clock for it: the planners dirtied by the
    /// invalidations since the previous step and those due at the tick run; with none, the step is the clock read and
    /// the next Run alone. A player speed request with no hold is the same quiet step, ending in the Run at the
    /// adopted speed.
    /// </summary>
    Review,

    /// <summary>
    /// Any other hold (a hazard, a game pause, the brain's own coupled-order or autosave hold) or an authority change:
    /// an early review. The planners that read an invalidated family run; a hold carrying no evidence, or an
    /// authority change, runs every planner.
    /// </summary>
    Wake,

    /// <summary>
    /// The step after one that did not complete (a refused resume, a fault, a cancelled call) or that left the clock
    /// held with nothing to wake the next one: at once when the previous step completed, after the watchdog's
    /// back-off when it did not. It runs the dirty and due planners.
    /// </summary>
    Retry,

    /// <summary>
    /// Runs every planner regardless of evidence: the first step, the step after idle or lost events, and the
    /// <see cref="StepReasons.DefaultFullStepEvery"/> safety net a review or retry step is promoted to.
    /// </summary>
    Full,
}

public static class StepReasons
{
    /// <summary>
    /// Bounds how long the due queue may drive the planners before a review step is promoted to a full one: the
    /// coarse reconciliation that re-runs every planner in case an invalidation was missed. Between full steps a
    /// review step runs only the planners due at the game tick (<c>PlannerEntry.ReviewEvery</c>, or the rounder's cadence for a family entry) or dirtied by an invalidation.
    /// </summary>
    public static readonly TimeSpan DefaultFullStepEvery = TimeSpan.FromMinutes(2);
}

/// <summary>
/// The evidence one step acts on. The host fills <see cref="Cause"/> and the wake fields; the scheduler fills
/// <see cref="TickAdvanced"/> from its status read and reports the reason it actually applied on
/// <see cref="ClockSchedulerResult.Reason"/>. A wake that followed a clock stop carries it in
/// <see cref="Stopped"/> and <see cref="StopAt"/>.
/// </summary>
public sealed record StepReason
{
    public StepCause Cause { get; init; }

    /// <summary>
    /// The fact families a wake's observation invalidations named; <see cref="Sections"/> the store sections the
    /// same events narrowed to. A reason carrying families alone is taken to dirty every section of theirs.
    /// </summary>
    public IReadOnlyList<FactFamily> Families { get; init; } = [];

    public IReadOnlyList<FactSection> Sections { get; init; } = [];

    /// <summary>The planners (by name) a wake marks due directly, for intent no fact family carries (the player saved goals).</summary>
    public IReadOnlyList<string> Planners { get; init; } = [];

    /// <summary>Set when a wake carried an authority change.</summary>
    public bool Authority { get; init; }

    /// <summary>Whether the game tick moved since the previous step's status read (true on the first step).</summary>
    public bool TickAdvanced { get; init; }

    /// <summary>Set when the wake followed a hold entry (the review tick, a hazard, a game pause or a brain hold); the step reports it in its telemetry.</summary>
    public bool Stopped { get; init; }

    /// <summary>When the clock entered the earliest pending hold, for the hold-to-step latency; null when unknown.</summary>
    public DateTimeOffset? StopAt { get; init; }

    public override string ToString()
    {
        var b = new StringBuilder(Wire.Name(Cause));
        if (TickAdvanced)
        {
            b.Append(" tick_advanced");
        }
        if (Authority)
        {
            b.Append(" authority");
        }
        foreach (var family in Families)
        {
            b.Append(' ').Append(Wire.Name(family));
        }
        foreach (var section in Sections)
        {
            b.Append(' ').Append(Wire.Name(section));
        }
        foreach (var planner in Planners)
        {
            b.Append(' ').Append(planner);
        }
        return b.ToString();
    }
}
