namespace RimGovernor.Brain.Policy;

// The facts and verdict of the pure watch decision (EvaluateClockWatch): whether the held clock may Run and under
// which watch policy. The clock is the continuous Run/Hold clock.

public enum ClockWatchState
{
    None,

    [WireName("running")]
    Running,

    [WireName("held")]
    Held,
}

public sealed record ClockWatchStatus
{
    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public ClockWatchState State { get; init; }

    public Fact<bool> NativeTickBoundary { get; init; }
}

public sealed record ClockWatchFacts
{
    public GenerationSnapshot Current { get; init; }

    public Tick Tick { get; init; }

    /// <summary>How long ago the decision's read began and how long ago it was observed (the caller's clock, never read here); the read began no later than it was observed.</summary>
    public TimeSpan SinceStarted { get; init; }

    public TimeSpan SinceObserved { get; init; }

    public required EmergencySnapshot Emergency { get; init; }

    /// <summary>Whether the brain has opened the colony events native pushes; before it, nothing it plans has seen them.</summary>
    public bool EventsOpen { get; init; }

    public ClockWatchStatus Status { get; init; } = new();

    /// <summary>Whether the military/combat Need holds an admitted plan with open work. Only then may live hostiles be watched instead of refused.</summary>
    public Fact<bool> CombatPlan { get; init; }

    /// <summary>
    /// Whether the defense planner, at this tick, found no eligible squad for the emergency's threats. A
    /// hostile building it cannot answer is then watched like a distant one, so the colony keeps running around
    /// it, instead of holding the clock for good, and a colonist's aggressive break it cannot subdue is watched
    /// so the break ends with ticks; a hostile pawn still holds, a raid must not auto-advance. Unknown means the
    /// planner has not reported and the building or breaker holds.
    /// </summary>
    public Fact<bool> SquadUnanswered { get; init; }

    /// <summary>
    /// Whether the threat's sheltering response is complete (ShelterHeld): every undrafted colonist is
    /// restricted to the Safe area. A hostile pawn is then watched in a combat watch instead of refused, so
    /// the sheltered colony waits it out.
    /// </summary>
    public Fact<bool> Sheltered { get; init; }

    /// <summary>
    /// The live prey of an open hunt fight (the squad hunt of the military/combat Need): no hostile stands, so the watch
    /// is a combat one that watches them.
    /// </summary>
    public EquatableArray<PawnId> HuntPrey { get; init; }

    /// <summary>The hungry colonists the last Run sent acknowledged (WatchPolicy.acknowledged_hungry_colonist_ids).</summary>
    public EquatableArray<PawnId> AcknowledgedHungry { get; init; }

    /// <summary>
    /// The fact the colony's first layout plan waits for (the last review's RoundsResult.FirstPlanUnread, #3652): the
    /// clock stays held while it is not empty, so no game time passes before the plan exists.
    /// </summary>
    public string FirstPlanUnread { get; init; } = "";

    /// <summary>
    /// Whether an urgent build placed since the last rounds review waits for a review to compare its sites (#3537): the
    /// clock stays held meanwhile, so the review binds them before deferred colonists build them by vanilla work.
    /// </summary>
    public bool UrgentBuilds { get; init; }
}

/// <summary>The bound the decision's facts must be fresh within.</summary>
public sealed record ClockWatchLimits
{
    public TimeSpan MaxAge { get; init; }
}

public enum ClockWatchMode
{
    None,

    [WireName("colony")]
    Colony,

    [WireName("combat")]
    Combat,
}

public enum ClockWatchReason
{
    None,

    [WireName("stale_facts")]
    Stale,

    [WireName("unknown_facts")]
    Unknown,

    [WireName("unsafe_colony")]
    Unsafe,

    [WireName("unreviewed_events")]
    Unreviewed,

    /// <summary>The colony's first layout plan waits for a station fact the review has not read (#3652).</summary>
    [WireName("first_plan")]
    FirstPlan,

    /// <summary>An urgent build's placed sites wait for a rounds review to compare them (#3537).</summary>
    [WireName("urgent_build")]
    UrgentBuild,
}

/// <summary>
/// The verdict on a Run. <see cref="Hostiles"/> lists, sorted, the live undowned threats a combat watch
/// acknowledges; it is empty in colony mode. <see cref="Downed"/> lists, sorted, the colonists already known
/// downed: the native watcher holds for any unacknowledged downed colonist, so an unacknowledged known casualty
/// would hold the clock again at once and neither the fight nor the rescue could finish. A colonist who goes down
/// while running still holds it.
/// </summary>
public sealed record ClockWatchDecision
{
    public bool Run { get; init; }

    public EquatableArray<ClockWatchReason> Refused { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public ClockWatchMode Mode { get; init; }

    public EquatableArray<PawnId> Hostiles { get; init; }

    public EquatableArray<PawnId> Downed { get; init; }

    /// <summary>
    /// The colonists the Run acknowledges as at or past the hunger hold category, sorted. Only a resume of a held
    /// clock adds to it: the step that planned the resume read the held world, so it saw them. A Run that adjusts a
    /// running clock keeps only those already acknowledged, so a colonist who got hungry since the step read still
    /// holds the clock and gets a review under it.
    /// </summary>
    public EquatableArray<PawnId> Hungry { get; init; }
}
