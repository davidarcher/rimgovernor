namespace RimGovernor.Brain.Policy;

// The facts and verdict of the pure watch decision (EvaluateClockWatch): whether the held clock may Run and under
// which watch policy. Go: clock_window_types.go, reduced to the continuous Run/Hold clock.

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

    /// <summary>Whether colony events or a loss of them arrived after the step being decided read the clock.</summary>
    public bool EventsSinceStep { get; init; }

    public ClockWatchStatus Status { get; init; } = new();

    /// <summary>Whether the military/combat Need holds an admitted plan with open work. Only then may live hostiles be watched instead of refused.</summary>
    public Fact<bool> CombatPlan { get; init; }

    /// <summary>
    /// Whether the defense planner, at this tick, found no eligible squad for the emergency's threats. A
    /// hostile building it cannot answer is then watched like a distant one, so the colony keeps running around
    /// it, instead of holding the clock for good; a hostile pawn still holds, a raid must not auto-advance.
    /// Unknown means the planner has not reported and the building holds.
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
}
