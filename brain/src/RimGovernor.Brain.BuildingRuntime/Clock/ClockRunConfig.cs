namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What the run driver needs beyond the scheduler's own configuration: the speed it asks the clock to run at, the
/// base watch policy every Run carries, the autosave and the call bounds.
/// </summary>
public sealed record ClockRunConfig
{
    /// <summary>The speed the clock runs at; a change reaches the clock as a Run at the new speed.</summary>
    public required ClockTargetSpeed TargetSpeed { get; init; }

    /// <summary>
    /// The live target a player's in-game speed request (<see cref="PbClock.Status.PlayerRequest"/>) sets,
    /// as the player API's speed route does: <see cref="TargetSpeed"/> itself, except under test acceleration, whose
    /// pinned run speed no request reaches.
    /// </summary>
    public required ClockTargetSpeed PlayerSpeed { get; init; }

    /// <summary>The colony watch policy with the hazard thresholds; each Run adds the acknowledgements the watch decision names.</summary>
    public required PbClock.WatchPolicy Policy { get; init; }

    /// <summary>Saves the game while the clock is held for it; null saves nothing.</summary>
    public Func<PbCommon.Identity, long, CancellationToken, Task>? Autosave { get; init; }

    /// <summary>
    /// One in-game day of game ticks: the conservative default autosave cadence, a guess that is unmeasured. A save
    /// costs a pause and a governor-state flush; the loss it bounds is the intent written since the last save, so the
    /// right interval needs a measured save duration and a tolerable loss (#3169 baseline), neither of which exists yet.
    /// </summary>
    public const long DefaultAutosaveEveryTicks = GameTime.TicksPerDay;

    /// <summary>The game ticks between autosave holds, measured on the game clock from the previous autosave hold; 0 holds at every running step.</summary>
    public long AutosaveEveryTicks { get; init; } = DefaultAutosaveEveryTicks;

    /// <summary>
    /// The review cadence: every Run names the tick native holds at for <see cref="PbClock.HoldReason.Review"/>, this
    /// many game ticks after the status tick the run was decided against. One number, no admission: 600 ticks is ten
    /// in-game seconds, about 100 reviews per in-game day, under a second of wall each at Ultrafast (#3556). A hazard
    /// hold is an early review and the Run that ends it names the next one from its own tick.
    /// </summary>
    public const long DefaultReviewEveryTicks = 600;

    /// <summary>The game ticks from a Run's status tick to the review hold it names.</summary>
    public long ReviewEveryTicks { get; init; } = DefaultReviewEveryTicks;

    /// <summary>The longest a combat hold may take to resume before the gate trips (unmeasured default, #3180).</summary>
    public TimeSpan CombatResumeBudget { get; init; } = ClockCombatStops.ResumeBudget;

    /// <summary>The speed ceiling while a fight is open once the gate has tripped (unmeasured default, #3180).</summary>
    public PbClock.Speed CombatFallbackSpeed { get; init; } = ClockCombatStops.FallbackSpeed;

    /// <summary>The longest the clock may go without a heartbeat before its ceiling drops to Normal is five seconds; the driver beats this often.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The bound of one native call the event consumer and the driver make.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
