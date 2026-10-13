namespace RimGovernor.Brain.Policy;

// Go: clock_window_types.go

public sealed record ClockWindowReview
{
    public ulong Revision { get; init; }

    public long Captured { get; init; }

    public long Reviewed { get; init; }

    public long Acknowledged { get; init; }

    public Fact<bool> HasHolds { get; init; }
}

public enum ClockWindowState
{
    None,

    [WireName("never_started")]
    NeverStarted,

    [WireName("running")]
    Running,

    [WireName("stopping")]
    Stopping,

    [WireName("stopped")]
    Stopped,
}

public sealed record ClockWindowStatus
{
    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public ClockWindowState State { get; init; }

    public Fact<bool> ActualPaused { get; init; }

    public Fact<bool> NativeTickBoundary { get; init; }

    public Fact<bool> DurableEvents { get; init; }

    public Fact<long> NewestCursor { get; init; }
}

public sealed record ClockWindowObligations
{
    public Fact<bool> Complete { get; init; }

    public Fact<bool> OwnedEpochPending { get; init; }

    public Fact<bool> UnknownStartPending { get; init; }
}

public sealed record ClockWindowFacts
{
    public GenerationSnapshot Current { get; init; }

    public Tick Tick { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset ObservedAt { get; init; }

    public required EmergencySnapshot Emergency { get; init; }

    public ClockWindowReview Review { get; init; } = new();

    public ClockWindowStatus Status { get; init; } = new();

    public ClockWindowObligations Obligations { get; init; } = new();

    public Fact<bool> WorkRemaining { get; init; }

    /// <summary>Whether the ActiveCombat goal holds an admitted plan with open work. Only then may live hostiles be watched instead of refused.</summary>
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
    /// restricted to the Safe area. A hostile pawn is then watched in a combat window instead of refused, so
    /// the sheltered colony waits it out.
    /// </summary>
    public Fact<bool> Sheltered { get; init; }

    /// <summary>
    /// The live prey of an open hunt fight (the hunt origin of ActiveCombat): no hostile stands, so the window
    /// is a combat one that watches them, and the combat budget wakes the fight's next decision.
    /// </summary>
    public EquatableArray<PawnId> HuntPrey { get; init; }
}

/// <summary>
/// The limits a window is admitted under. <see cref="CombatMaxTicks"/> bounds a combat window; zero means the
/// colony budget. A raid is re-planned between short windows, so the combat budget never exceeds MaxTicks.
/// </summary>
public sealed record ClockWindowLimits
{
    public DateTimeOffset Now { get; init; }

    public TimeSpan MaxAge { get; init; }

    public uint MaxTicks { get; init; }

    public uint CombatMaxTicks { get; init; }
}

public enum ClockWindowMode
{
    None,

    [WireName("colony")]
    Colony,

    [WireName("combat")]
    Combat,
}

public enum ClockWindowReason
{
    None,

    [WireName("stale_facts")]
    Stale,

    [WireName("unknown_facts")]
    Unknown,

    [WireName("unsafe_colony")]
    Unsafe,

    [WireName("outstanding_epoch")]
    Outstanding,

    [WireName("unreviewed_events")]
    Unreviewed,

    [WireName("interruption")]
    Interrupted,

    [WireName("not_paused")]
    NotPaused,

    [WireName("no_work")]
    NoWork,

    [WireName("invalid_limits")]
    InvalidLimits,
}

/// <summary>
/// The verdict on a window. <see cref="Hostiles"/> lists, sorted, the live undowned threats a combat window
/// acknowledges; it is empty in colony mode. <see cref="Downed"/> lists, sorted, the colonists already known
/// downed at admission: the native watcher stops a window for any unacknowledged downed colonist, so an
/// unacknowledged known casualty stopped every window at zero ticks and neither the fight nor the rescue could
/// finish. A colonist who goes down during the window still stops it.
/// </summary>
public sealed record ClockWindowDecision
{
    public bool Admitted { get; init; }

    public EquatableArray<ClockWindowReason> Refused { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public ulong ReviewRevision { get; init; }

    public long CapturedCursor { get; init; }

    public uint MaxTicks { get; init; }

    public ClockWindowMode Mode { get; init; }

    public EquatableArray<PawnId> Hostiles { get; init; }

    public EquatableArray<PawnId> Downed { get; init; }
}
