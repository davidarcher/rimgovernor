using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Why the scheduler stepped.</summary>
public enum StepCause
{
    /// <summary>
    /// The host's cadence firing with nothing captured. It runs planners only when the game tick moved since
    /// the last step; otherwise the step is the admission tail alone.
    /// </summary>
    Timer,

    /// <summary>
    /// Committed outcome evidence: the planners that dispatch the outcomes' kinds and the planners that read an
    /// invalidated family run; an authority change runs every planner.
    /// </summary>
    Wake,

    /// <summary>A step that follows a run that just ended: every planner re-plans.</summary>
    Settled,

    /// <summary>
    /// Runs every planner regardless of evidence; it is also the <see cref="StepReasons.DefaultFullStepEvery"/>
    /// safety net a timer step is promoted to.
    /// </summary>
    Full,
}

public static class StepReasons
{
    /// <summary>
    /// Bounds how long the due queue may drive the planners before a timer step is promoted to a full one: the
    /// coarse reconciliation that re-runs every planner in case an invalidation was missed. Between full steps a
    /// timer step runs only the planners due at the game tick (<c>PlannerEntry.ReviewEvery</c>) or dirtied by a wake.
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

    /// <summary>Set when a wake carried an authority change.</summary>
    public bool Authority { get; init; }

    /// <summary>Whether the game tick moved since the previous step's status read (true on the first step).</summary>
    public bool TickAdvanced { get; init; }

    /// <summary>Set when the wake followed a clock stop; the step reports it in its telemetry.</summary>
    public bool Stopped { get; init; }

    /// <summary>When the clock stopped, for the stop-to-step latency; null when unknown.</summary>
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
        return b.ToString();
    }
}

internal static class PlannerSelections
{
    /// <summary>
    /// Decides which catalog planners a step for <paramref name="reason"/> runs at <paramref name="tick"/>, over
    /// a copy of the due queue <paramref name="q"/>: a full step runs every planner; a settled step (a run
    /// just ended) marks every planner and runs those not waiting on a dependency; a timer step runs the planners
    /// dirty or due at the tick; a wake or live step folds its evidence first (the consumers of the dirty
    /// sections, everything on an authority change, everything on a wake carrying nothing selectable). The
    /// result's pick is null when every planner runs.
    /// </summary>
    public static PlannerSelectionResult Of(StepReason reason, PlannerQueue? q, long tick)
    {
        var copy = q.CloneOrNew();
        switch (reason.Cause)
        {
            case StepCause.Full:
                return copy.Selection(tick, true, false, null);
            case StepCause.Settled:
                return copy.Selection(tick, false, true, null);
            case StepCause.Timer:
                return copy.Selection(tick, false, false, null);
            case StepCause.Wake:
                copy.Wake(reason);
                return copy.Selection(tick, false, false, null);
            default:
                return copy.Selection(tick, true, false, null);
        }
    }
}
