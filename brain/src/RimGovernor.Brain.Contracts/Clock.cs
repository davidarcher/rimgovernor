using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Clock;

/// <summary>
/// Requested epoch speeds. Ultrafast is an ordinary speed on the wire; the
/// native dev tick boost behind it is test acceleration
/// (StartRequest.test_acceleration), which only a game launched with
/// -rimgovernor-test-acceleration (acceptance profiles) admits.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<Speed>))]
public enum Speed
{
    [WireName("SPEED_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("SPEED_NORMAL")]
    Normal = 1,
    [WireName("SPEED_FAST")]
    Fast = 2,
    [WireName("SPEED_SUPERFAST")]
    Superfast = 3,
    [WireName("SPEED_ULTRAFAST")]
    Ultrafast = 4,
}

[JsonConverter(typeof(WireEnumConverter<ObservedSpeed>))]
public enum ObservedSpeed
{
    [WireName("OBSERVED_SPEED_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("OBSERVED_SPEED_PAUSED")]
    Paused = 1,
    [WireName("OBSERVED_SPEED_NORMAL")]
    Normal = 2,
    [WireName("OBSERVED_SPEED_FAST")]
    Fast = 3,
    [WireName("OBSERVED_SPEED_SUPERFAST")]
    Superfast = 4,
    [WireName("OBSERVED_SPEED_ULTRAFAST")]
    Ultrafast = 5,
}

/// <summary>
/// How a running Ultrafast epoch paces its ticks. FIXED is the speed's
/// own rate. PLAYER_ACCELERATED raises Ultrafast's ticks per frame toward the
/// boosted rate while the frame's tick work stays inside native's 30 ms frame
/// budget, so input, rendering and control dispatch keep their share of each
/// frame. It needs no test launch.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<Pacing>))]
public enum Pacing
{
    [WireName("PACING_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("PACING_FIXED")]
    Fixed = 1,
    [WireName("PACING_PLAYER_ACCELERATED")]
    PlayerAccelerated = 2,
}

/// <summary>
/// What bounds the running epoch's tick rate right now, most binding
/// first: the blind-tick regulator, the owner's
/// max_ticks_per_second (the controller's backoff), the frame budget; else
/// the pacing runs at its ceiling (ACCELERATED) or the speed's own rate
/// (FIXED).
/// </summary>
[JsonConverter(typeof(WireEnumConverter<PacingReason>))]
public enum PacingReason
{
    [WireName("PACING_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("PACING_REASON_FIXED")]
    Fixed = 1,
    [WireName("PACING_REASON_ACCELERATED")]
    Accelerated = 2,
    [WireName("PACING_REASON_FRAME_BUDGET")]
    FrameBudget = 3,
    [WireName("PACING_REASON_REGULATED")]
    Regulated = 5,
    [WireName("PACING_REASON_CEILING")]
    Ceiling = 6,
}

[JsonConverter(typeof(WireEnumConverter<WatchMode>))]
public enum WatchMode
{
    [WireName("WATCH_MODE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("WATCH_MODE_COLONY")]
    Colony = 1,
    [WireName("WATCH_MODE_COMBAT")]
    Combat = 2,
}

[JsonConverter(typeof(WireEnumConverter<StopReason>))]
public enum StopReason
{
    [WireName("STOP_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("STOP_REASON_REQUESTED_PAUSE")]
    RequestedPause = 1,
    [WireName("STOP_REASON_TICK_BUDGET")]
    TickBudget = 2,
    [WireName("STOP_REASON_SESSION_CHANGED")]
    SessionChanged = 3,
    [WireName("STOP_REASON_UNAVAILABLE")]
    Unavailable = 4,
    [WireName("STOP_REASON_EXTERNAL_PAUSE")]
    ExternalPause = 5,
    [WireName("STOP_REASON_LETTER_PAUSE")]
    LetterPause = 6,
    [WireName("STOP_REASON_EXTERNAL_SPEED_CHANGED")]
    ExternalSpeedChanged = 7,
    [WireName("STOP_REASON_LEASE_EXPIRED")]
    LeaseExpired = 8,
    [WireName("STOP_REASON_WATCHER_ERROR")]
    WatcherError = 9,
    [WireName("STOP_REASON_EVENT_JOURNAL_ERROR")]
    EventJournalError = 10,
    [WireName("STOP_REASON_FORCE_PAUSED")]
    ForcePaused = 11,
    [WireName("STOP_REASON_COLONY_NAMING")]
    ColonyNaming = 12,
    [WireName("STOP_REASON_START_REFUSED")]
    StartRefused = 13,
    [WireName("STOP_REASON_NOTIFICATION_BATCH")]
    NotificationBatch = 14,
    [WireName("STOP_REASON_MEDICAL_REST_CHANGED")]
    MedicalRestChanged = 15,
    [WireName("STOP_REASON_HOSTILE")]
    Hostile = 16,
    [WireName("STOP_REASON_COLONIST_DOWNED")]
    ColonistDowned = 17,
    [WireName("STOP_REASON_PREDATOR_HUNT")]
    PredatorHunt = 18,
    [WireName("STOP_REASON_HUNTING_ROUTE_UNSAFE")]
    HuntingRouteUnsafe = 19,
    [WireName("STOP_REASON_COLONIST_HEALTH")]
    ColonistHealth = 20,
    [WireName("STOP_REASON_COLONIST_INJURY")]
    ColonistInjury = 21,
    /// <summary>
    /// An armed watched attempt reached a terminal outcome; the epoch stopped at
    /// the next tick boundary so the controller can react at once.
    /// </summary>
    [WireName("STOP_REASON_WATCH_LATCHED")]
    WatchLatched = 22,
    /// <summary>
    /// A force-pausing Verse.Dialog_NodeTree the game opened by itself holds the
    /// clock; PauseEvidence.dialog names it and ColonyFactsSnapshot.dialog lists
    /// its options for Operations.AnswerDialog.
    /// </summary>
    [WireName("STOP_REASON_DIALOG_PAUSE")]
    DialogPause = 23,
    /// <summary>
    /// An armed combat event (WatchPolicy.combat_stop_events) happened; the
    /// epoch stopped at the boundary of the tick it happened on.
    /// </summary>
    [WireName("STOP_REASON_COMBAT_EVENT")]
    CombatEvent = 24,
}

/// <summary>
/// The combat events a WATCH_MODE_COMBAT epoch can be armed to stop on.
/// Native hooks see each one on the tick it happens and stop the
/// epoch at that tick's boundary. Aim warmup, shots and ordinary damage are
/// never stops.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CombatEvent>))]
public enum CombatEvent
{
    [WireName("COMBAT_EVENT_UNSPECIFIED")]
    Unspecified = 0,
    /// <summary>
    /// A colonist was downed or killed.
    /// </summary>
    [WireName("COMBAT_EVENT_DOWNED")]
    Downed = 1,
    /// <summary>
    /// A colonist took a serious hit: summary health or a vital part under its
    /// floor, bleeding past the rate floor, or one blow past the single-hit floor.
    /// </summary>
    [WireName("COMBAT_EVENT_SERIOUS_INJURY")]
    SeriousInjury = 2,
    /// <summary>
    /// A colonist's or a friendly's shield broke.
    /// </summary>
    [WireName("COMBAT_EVENT_SHIELD_BROKEN")]
    ShieldBroken = 3,
    /// <summary>
    /// A hostile's weapon came within range of a colonist or player turret, or
    /// a colonist's or turret's weapon came within range of a hostile; each
    /// direction once per hostile per combat (melee reach for a melee weapon).
    /// </summary>
    [WireName("COMBAT_EVENT_ENTERED_RANGE")]
    EnteredRange = 4,
    /// <summary>
    /// A hostile began a melee job on a colonist, or a colonist on a hostile.
    /// </summary>
    [WireName("COMBAT_EVENT_MELEE_CONTACT")]
    MeleeContact = 5,
    /// <summary>
    /// A hostile launched an explosive projectile at or near a colonist.
    /// </summary>
    [WireName("COMBAT_EVENT_EXPLOSIVE_LAUNCHED")]
    ExplosiveLaunched = 6,
    /// <summary>
    /// A hostile lord changed toil (preparing -&gt; assault, siege -&gt; attack, flee).
    /// </summary>
    [WireName("COMBAT_EVENT_RAID_PHASE")]
    RaidPhase = 7,
    /// <summary>
    /// A hostile pawn spawned on the map (walk-in, drop pod).
    /// </summary>
    [WireName("COMBAT_EVENT_HOSTILE_ARRIVED")]
    HostileArrived = 8,
    /// <summary>
    /// A player wall, door or turret was destroyed by damage.
    /// </summary>
    [WireName("COMBAT_EVENT_BREACH")]
    Breach = 9,
    /// <summary>
    /// A pawn on the map started a mental state (berserk, fleeing).
    /// </summary>
    [WireName("COMBAT_EVENT_MENTAL_BREAK")]
    MentalBreak = 10,
    /// <summary>
    /// A prison break started on the map.
    /// </summary>
    [WireName("COMBAT_EVENT_PRISON_BREAK")]
    PrisonBreak = 11,
}

[JsonConverter(typeof(WireEnumConverter<InjurySuppression>))]
public enum InjurySuppression
{
    [WireName("INJURY_SUPPRESSION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("INJURY_SUPPRESSION_NONE")]
    None = 1,
    [WireName("INJURY_SUPPRESSION_ACKNOWLEDGED")]
    Acknowledged = 2,
    [WireName("INJURY_SUPPRESSION_COOLDOWN")]
    Cooldown = 3,
}

/// <summary>
/// A group of observation reads that go stale together. Definitions and world
/// facts survive a tick advance; the rest are facts of one paused tick.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<FactFamily>))]
public enum FactFamily
{
    [WireName("FACT_FAMILY_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("FACT_FAMILY_DEFINITIONS")]
    Definitions = 1,
    [WireName("FACT_FAMILY_WORLD")]
    World = 2,
    [WireName("FACT_FAMILY_IDENTITY")]
    Identity = 3,
    [WireName("FACT_FAMILY_COLONY")]
    Colony = 4,
    [WireName("FACT_FAMILY_PAWNS")]
    Pawns = 5,
    [WireName("FACT_FAMILY_EMERGENCY")]
    Emergency = 6,
    [WireName("FACT_FAMILY_ROOMS")]
    Rooms = 7,
    [WireName("FACT_FAMILY_RESEARCH")]
    Research = 8,
}

/// <summary>
/// ForcePauseKind is what held the clock: a long event (an autosave) or a pause with no window behind it.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ForcePauseKind>))]
public enum ForcePauseKind
{
    [WireName("FORCE_PAUSE_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("FORCE_PAUSE_KIND_LONG_EVENT")]
    LongEvent = 1,
    [WireName("FORCE_PAUSE_KIND_TRANSIENT")]
    Transient = 2,
}

public sealed record WatchPolicy
{
    public WatchMode? Mode { get; init; }

    public float? HealthDropFraction { get; init; }

    public float? MinHealthFraction { get; init; }

    public float? HostileWithin { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AcknowledgedHostileIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AcknowledgedDownedColonistIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AcknowledgedInjuredColonistIds { get; init; }

    public uint? InjuryStopCooldownMs { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> SurgicalRecoveryIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> MedicalRestIds { get; init; }

    /// <summary>
    /// WATCH_MODE_COMBAT only: the combat events that stop the epoch on the
    /// tick they happen (STOP_REASON_COMBAT_EVENT). Distinct, specified;
    /// empty arms none. Refused outside combat mode.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CombatEvent>))]
    public EquatableArray<CombatEvent> CombatStopEvents { get; init; }

    /// <summary>
    /// Go-authored hazard thresholds; all required, each finite and positive.
    /// One blow of at least serious_single_hit_damage, summary health crossing
    /// under serious_summary_health_floor (also the floor a resting patient must
    /// stay over to remain eligible), a total bleed rate crossing over
    /// serious_bleed_rate_floor, or a vital part hit under serious_vital_part_floor
    /// of its health is a serious injury. An explosive landing within its blast
    /// radius plus explosive_near_margin_cells of a colonist is near them;
    /// melee_reach_cells is the range assumed for a pawn with no ranged weapon.
    /// A new wound keeps the stop when it bleeds the colonist out within
    /// injury_severity_floor_ticks or is life-threatening. A hunt is unsafe when
    /// its route passes within predator_margin_cells of a wild predator.
    /// </summary>
    public float? SeriousSingleHitDamage { get; init; }

    public float? SeriousSummaryHealthFloor { get; init; }

    public float? SeriousBleedRateFloor { get; init; }

    public float? SeriousVitalPartFloor { get; init; }

    public float? ExplosiveNearMarginCells { get; init; }

    public float? MeleeReachCells { get; init; }

    public uint? InjurySeverityFloorTicks { get; init; }

    public float? PredatorMarginCells { get; init; }
}

/// <summary>
/// Epoch ownership is distinct from automation authority and viewer/input leases.
/// </summary>
public sealed record EpochOwner
{
    public string? ControllerSessionId { get; init; }

    public long? Epoch { get; init; }
}

public sealed record StartRequest
{
    public Authority.WritePrecondition? Authority { get; init; }

    public Speed? Speed { get; init; }

    public WatchPolicy? Policy { get; init; }

    public uint? LeaseMs { get; init; }

    public uint? MaxTicks { get; init; }

    /// <summary>
    /// Enable the native tick boost for this epoch. Requires SPEED_ULTRAFAST and
    /// a game launched with test acceleration (Status.test_acceleration_available);
    /// refused otherwise. An accelerated epoch cannot change speed, only pause
    /// or change its ceiling.
    /// </summary>
    public bool? TestAcceleration { get; init; }

    /// <summary>
    /// Blind-tick regulator. Blind ticks are the ticks between the
    /// controller's last read (a status, events or bundle read, or the oldest
    /// journal row it has not yet acknowledged with an after_cursor) and the
    /// current tick. Past this budget native throttles the epoch toward Normal
    /// at the next tick boundary and ramps back up once the controller catches
    /// up (immediate decrease, multiplicative increase), journaling both
    /// transitions as SpeedChanged without ending the epoch. Zero or absent
    /// leaves the epoch unregulated.
    /// </summary>
    public uint? BlindTickBudget { get; init; }

    /// <summary>
    /// Continuous ceiling on the epoch's tick rate, beneath the requested
    /// speed's own rate; an accelerated epoch admits a live change through
    /// SpeedRequest. Zero or absent is the speed's own rate.
    /// </summary>
    public uint? MaxTicksPerSecond { get; init; }

    /// <summary>
    /// PACING_PLAYER_ACCELERATED requires SPEED_ULTRAFAST and excludes
    /// test_acceleration; absent is PACING_FIXED. Native paces it against its
    /// own 30 ms frame budget (Epoch.frame_budget_ms).
    /// </summary>
    public Pacing? Pacing { get; init; }
}

public sealed record OwnedRequest
{
    public Common.Identity? Identity { get; init; }

    public EpochOwner? Owner { get; init; }
}

public sealed record RenewRequest
{
    public OwnedRequest? Epoch { get; init; }

    public Authority.WritePrecondition? Authority { get; init; }

    public uint? LeaseMs { get; init; }
}

public sealed record SpeedRequest
{
    public OwnedRequest? Epoch { get; init; }

    public Authority.WritePrecondition? Authority { get; init; }

    public Speed? Speed { get; init; }

    /// <summary>
    /// The new ceiling (StartRequest.max_ticks_per_second); absent keeps the
    /// epoch's. An accelerated epoch admits this with SPEED_ULTRAFAST.
    /// </summary>
    public uint? MaxTicksPerSecond { get; init; }
}

public sealed record StatusRequest
{
    public Common.Identity? Identity { get; init; }
}

public sealed record Epoch
{
    public EpochOwner? Owner { get; init; }

    public Common.ObservationContext? Origin { get; init; }

    public Speed? RequestedSpeed { get; init; }

    public WatchPolicy? Policy { get; init; }

    public long? StartTick { get; init; }

    public long? TickDeadline { get; init; }

    public uint? LeaseRemainingMs { get; init; }

    public long? LastTick { get; init; }

    public bool? TestAcceleration { get; init; }

    public uint? BlindTickBudget { get; init; }

    public uint? MaxTicksPerSecond { get; init; }

    /// <summary>
    /// The ceiling the blind-tick regulator holds right now; zero when it is
    /// not throttling.
    /// </summary>
    public uint? RegulatedTicksPerSecond { get; init; }

    public Pacing? Pacing { get; init; }

    public uint? FrameBudgetMs { get; init; }

    /// <summary>
    /// The tick rate the pacing holds right now (60 x the multiplier the game
    /// ticks at), and what bounds it.
    /// </summary>
    public uint? PacedTicksPerSecond { get; init; }

    public PacingReason? PacingReason { get; init; }
}

public sealed record Running
{
    public Epoch? Epoch { get; init; }
}

public sealed record Stopping
{
    public Epoch? Epoch { get; init; }

    public StopReason? PendingReason { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// Failed pause stays armed. This state never grants permission to resume.
    /// </summary>
    public bool? ActualPaused { get; init; }
}

public sealed record Stopped
{
    public Epoch? Epoch { get; init; }

    public StopReason? Reason { get; init; }

    public string? Detail { get; init; }

    public bool? ActualPaused { get; init; }

    public bool? PauseVerified { get; init; }

    public bool? PauseRequested { get; init; }

    public long? StoppedAtUnixMs { get; init; }

    /// <summary>
    /// STOP_REASON_COMBAT_EVENT only: which armed event stopped the epoch.
    /// </summary>
    public CombatEvent? CombatEvent { get; init; }
}

public sealed record NeverStarted;

public sealed record Status
{
    public Common.ObservationContext? Context { get; init; }

    public StatusState? State { get; init; }

    public bool? NativeTickBoundary { get; init; }

    public bool? DurableEvents { get; init; }

    public long? NewestCursor { get; init; }

    public string? WatcherError { get; init; }

    public ulong? ForcePauseWaitingMs { get; init; }

    public ForcePauseKind? ForcePauseKind { get; init; }

    public long? MaxProbeTickGap { get; init; }

    public ulong? ProbeCount { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Alert>))]
    public EquatableArray<Alert> BaselineAlerts { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Injury>))]
    public EquatableArray<Injury> SuppressedInjuries { get; init; }

    public ObservedSpeed? ObservedSpeed { get; init; }

    public bool? ActualPaused { get; init; }

    /// <summary>
    /// The launch admits StartRequest.test_acceleration.
    /// </summary>
    public bool? TestAccelerationAvailable { get; init; }

    /// <summary>
    /// Wall time the supervisor's own stop/start transitions account for in
    /// this game session, on a monotonic clock: paused_ms is the time from
    /// each stop to the next start (the open gap included while stopped),
    /// running_ms the time each epoch ran. Gaps no controller observed count.
    /// Both reset when the loaded game changes.
    /// </summary>
    public ulong? PausedMs { get; init; }

    public ulong? RunningMs { get; init; }

    /// <summary>
    /// Supervisor probe accounting, cumulative for the loaded game
    /// session like paused_ms: wall time the hazard probe (probe_elapsed_ms,
    /// probe_total passes) and the fact-change digests (digest_elapsed_ms,
    /// digest_total passes) took on the main thread, and the widest tick gap
    /// between consecutive probes any epoch of the session saw.
    /// </summary>
    public double? ProbeElapsedMs { get; init; }

    public double? DigestElapsedMs { get; init; }

    public ulong? ProbeTotal { get; init; }

    public ulong? DigestTotal { get; init; }

    public long? SessionMaxProbeTickGap { get; init; }

    /// <summary>
    /// Per hazard class: the declared maximum detection gap in ticks and the
    /// widest gap observed this session.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<HazardGap>))]
    public EquatableArray<HazardGap> HazardGaps { get; init; }

    /// <summary>
    /// Game ticks per wall second over the last ten seconds of frames, paused
    /// time included: what the player actually gets.
    /// </summary>
    public double? EffectiveTicksPerSecond { get; init; }

    /// <summary>
    /// Player acceleration's frame account, cumulative for the loaded
    /// game session: frames a player-accelerated epoch ticked, those whose
    /// tick work exceeded the epoch's frame budget, and the widest frame's
    /// tick work. A command queued for the main thread waits at most one
    /// frame's tick work.
    /// </summary>
    public ulong? PacedFrames { get; init; }

    public ulong? PacedFramesOverBudget { get; init; }

    public double? MaxPacedFrameMs { get; init; }

    /// <summary>
    /// The speed the player last chose in this loaded game: a speed
    /// set by anything but the supervisor itself, or by a fixture standing in
    /// for the player. Unset until one is chosen; pauses never count. The
    /// owner starts each window at it.
    /// </summary>
    public Speed? PlayerSpeed { get; init; }
}

/// <summary>The <c>state</c> choice of <see cref="Status"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatusState.RunningCase), "running")]
[JsonDerivedType(typeof(StatusState.StoppingCase), "stopping")]
[JsonDerivedType(typeof(StatusState.StoppedCase), "stopped")]
[JsonDerivedType(typeof(StatusState.NeverStartedCase), "neverStarted")]
[JsonDerivedType(typeof(StatusState.UnavailableCase), "unavailable")]
public abstract record StatusState
{
    private protected StatusState() { }

    /// <summary>The <c>Running</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Running? Running => (this as RunningCase)?.Value;

    public sealed record RunningCase(Running Value) : StatusState;

    public static implicit operator StatusState(Running value) => new RunningCase(value);

    /// <summary>The <c>Stopping</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Stopping? Stopping => (this as StoppingCase)?.Value;

    public sealed record StoppingCase(Stopping Value) : StatusState;

    public static implicit operator StatusState(Stopping value) => new StoppingCase(value);

    /// <summary>The <c>Stopped</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Stopped? Stopped => (this as StoppedCase)?.Value;

    public sealed record StoppedCase(Stopped Value) : StatusState;

    public static implicit operator StatusState(Stopped value) => new StoppedCase(value);

    /// <summary>The <c>NeverStarted</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public NeverStarted? NeverStarted => (this as NeverStartedCase)?.Value;

    public sealed record NeverStartedCase(NeverStarted Value) : StatusState;

    public static implicit operator StatusState(NeverStarted value) => new NeverStartedCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : StatusState;

    public static implicit operator StatusState(Common.Unavailable value) => new UnavailableCase(value);
}

/// <summary>
/// One hazard class the supervisor probe covers: its declared detection
/// bound in ticks (holds at every speed) and the widest gap observed.
/// </summary>
public sealed record HazardGap
{
    public string HazardClass { get; init; } = "";

    public long BoundTicks { get; init; }

    public long MaxTickGap { get; init; }

    /// <summary>
    /// The class also has a direct game hook that requests a probe on the
    /// next tick; max_tick_gap then measures hook to probe.
    /// </summary>
    public bool Hooked { get; init; }
}

public sealed record StatusReply
{
    public StatusReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="StatusReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatusReplyOutcome.StatusCase), "status")]
[JsonDerivedType(typeof(StatusReplyOutcome.FailureCase), "failure")]
public abstract record StatusReplyOutcome
{
    private protected StatusReplyOutcome() { }

    /// <summary>The <c>Status</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Status? Status => (this as StatusCase)?.Value;

    public sealed record StatusCase(Status Value) : StatusReplyOutcome;

    public static implicit operator StatusReplyOutcome(Status value) => new StatusCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : StatusReplyOutcome;

    public static implicit operator StatusReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record LongEventPending
{
    public string? Detail { get; init; }
}

public sealed record AppliedControl
{
    public Status? Status { get; init; }
}

public sealed record UncertainControl
{
    public Status? LastObserved { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// authorizing_owner (rimgovernor.authority.v1.Owner) was removed: with a
/// single bot process there is only ever one possible authorizing actor.
/// </summary>
public sealed record ControlReceipt
{
    public Common.AttemptKey? Attempt { get; init; }

    public Common.ObservationContext? AdmittedContext { get; init; }

    public ControlReceiptOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ControlReceipt"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ControlReceiptOutcome.AppliedCase), "applied")]
[JsonDerivedType(typeof(ControlReceiptOutcome.UncertainCase), "uncertain")]
public abstract record ControlReceiptOutcome
{
    private protected ControlReceiptOutcome() { }

    /// <summary>The <c>Applied</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public AppliedControl? Applied => (this as AppliedCase)?.Value;

    public sealed record AppliedCase(AppliedControl Value) : ControlReceiptOutcome;

    public static implicit operator ControlReceiptOutcome(AppliedControl value) => new AppliedCase(value);

    /// <summary>The <c>Uncertain</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public UncertainControl? Uncertain => (this as UncertainCase)?.Value;

    public sealed record UncertainCase(UncertainControl Value) : ControlReceiptOutcome;

    public static implicit operator ControlReceiptOutcome(UncertainControl value) => new UncertainCase(value);
}

/// <summary>
/// Admission failure has no clock effect. A changed or uncertain clock returns status.
/// </summary>
public sealed record ControlReply
{
    public ControlReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="ControlReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ControlReplyOutcome.ReceiptCase), "receipt")]
[JsonDerivedType(typeof(ControlReplyOutcome.FailureCase), "failure")]
[JsonDerivedType(typeof(ControlReplyOutcome.LongEventPendingCase), "longEventPending")]
public abstract record ControlReplyOutcome
{
    private protected ControlReplyOutcome() { }

    /// <summary>The <c>Receipt</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ControlReceipt? Receipt => (this as ReceiptCase)?.Value;

    public sealed record ReceiptCase(ControlReceipt Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(ControlReceipt value) => new ReceiptCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(Common.Failure value) => new FailureCase(value);

    /// <summary>The <c>LongEventPending</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public LongEventPending? LongEventPending => (this as LongEventPendingCase)?.Value;

    public sealed record LongEventPendingCase(LongEventPending Value) : ControlReplyOutcome;

    public static implicit operator ControlReplyOutcome(LongEventPending value) => new LongEventPendingCase(value);
}

public sealed record AttemptRequest
{
    public Common.Identity? Identity { get; init; }

    public Common.AttemptKey? Attempt { get; init; }
}

/// <summary>
/// Missing records never prove that a lost request had no effect.
/// </summary>
public sealed record AttemptUnknown;

public sealed record AttemptReply
{
    public AttemptReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="AttemptReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AttemptReplyOutcome.ReceiptCase), "receipt")]
[JsonDerivedType(typeof(AttemptReplyOutcome.UnknownCase), "unknown")]
[JsonDerivedType(typeof(AttemptReplyOutcome.FailureCase), "failure")]
public abstract record AttemptReplyOutcome
{
    private protected AttemptReplyOutcome() { }

    /// <summary>The <c>Receipt</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ControlReceipt? Receipt => (this as ReceiptCase)?.Value;

    public sealed record ReceiptCase(ControlReceipt Value) : AttemptReplyOutcome;

    public static implicit operator AttemptReplyOutcome(ControlReceipt value) => new ReceiptCase(value);

    /// <summary>The <c>Unknown</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public AttemptUnknown? Unknown => (this as UnknownCase)?.Value;

    public sealed record UnknownCase(AttemptUnknown Value) : AttemptReplyOutcome;

    public static implicit operator AttemptReplyOutcome(AttemptUnknown value) => new UnknownCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : AttemptReplyOutcome;

    public static implicit operator AttemptReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record Letter
{
    public string? Id { get; init; }

    public string? Label { get; init; }

    public string? DefName { get; init; }

    public bool? Negative { get; init; }
}

public sealed record TransientMessage
{
    public string? Id { get; init; }

    public string? TypeDef { get; init; }

    public string? Text { get; init; }

    public long? StartingTick { get; init; }

    public bool? Negative { get; init; }
}

public sealed record Notification
{
    public NotificationSource? Source { get; init; }
}

/// <summary>The <c>source</c> choice of <see cref="Notification"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NotificationSource.LetterCase), "letter")]
[JsonDerivedType(typeof(NotificationSource.MessageCase), "message")]
public abstract record NotificationSource
{
    private protected NotificationSource() { }

    /// <summary>The <c>Letter</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Letter? Letter => (this as LetterCase)?.Value;

    public sealed record LetterCase(Letter Value) : NotificationSource;

    public static implicit operator NotificationSource(Letter value) => new LetterCase(value);

    /// <summary>The <c>Message</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public TransientMessage? Message => (this as MessageCase)?.Value;

    public sealed record MessageCase(TransientMessage Value) : NotificationSource;

    public static implicit operator NotificationSource(TransientMessage value) => new MessageCase(value);
}

public sealed record NotificationBatch
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<Letter>))]
    public EquatableArray<Letter> Letters { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<TransientMessage>))]
    public EquatableArray<TransientMessage> Messages { get; init; }
}

public sealed record Alert
{
    public string? Key { get; init; }

    public string? Label { get; init; }

    public string? Priority { get; init; }
}

public sealed record PawnEvent
{
    public string? PawnId { get; init; }

    public string? Name { get; init; }

    public Common.Cell? Position { get; init; }

    public string? Reason { get; init; }
}

public sealed record Health
{
    public int? InjuryCount { get; init; }

    public float? Severity { get; init; }

    public float? BleedRate { get; init; }

    public float? BloodLoss { get; init; }

    public float? SummaryHealth { get; init; }
}

public sealed record Injury
{
    public PawnEvent? Pawn { get; init; }

    public Health? Before { get; init; }

    public Health? After { get; init; }

    public InjurySuppression? Suppression { get; init; }

    public uint? CooldownRemainingMs { get; init; }

    public bool? NewWound { get; init; }
}

public sealed record HealthThreshold
{
    public PawnEvent? Pawn { get; init; }

    public float? HealthAtStart { get; init; }

    public float? HealthNow { get; init; }

    public float? MinHealthFraction { get; init; }

    public float? HealthDropFraction { get; init; }
}

public sealed record HostilesCleared
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnEvent>))]
    public EquatableArray<PawnEvent> DownedHostiles { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnEvent>))]
    public EquatableArray<PawnEvent> DraftedColonists { get; init; }

    public int? ConsciousHostilesBefore { get; init; }

    public bool? AcrossEpochRestart { get; init; }
}

public sealed record BudgetReached
{
    public long? StartTick { get; init; }

    public long? TickDeadline { get; init; }

    public long? ActualTick { get; init; }
}

public sealed record PauseEvidence
{
    public bool? ActualPaused { get; init; }

    public bool? PauseRequested { get; init; }

    public bool? PauseVerified { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> ForcePausingWindowIds { get; init; }

    public bool? LongEventPending { get; init; }

    public Letter? Letter { get; init; }

    public Speed? RequestedSpeed { get; init; }

    public ObservedSpeed? ActualSpeed { get; init; }

    public DialogPause? Dialog { get; init; }
}

public sealed record DialogPause
{
    public int? WindowId { get; init; }

    public string? WindowType { get; init; }

    public string? Title { get; init; }
}

/// <summary>
/// Terminal progress of an armed watched attempt, latched at a tick boundary.
/// The outcome mirrors receipts.v1.Progress.
/// </summary>
public sealed record OperationOutcome
{
    public Common.AttemptKey? Attempt { get; init; }

    public long? LatchedTick { get; init; }

    public OperationOutcomeOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="OperationOutcome"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OperationOutcomeOutcome.CompletedCase), "completed")]
[JsonDerivedType(typeof(OperationOutcomeOutcome.UnsuccessfulCase), "unsuccessful")]
[JsonDerivedType(typeof(OperationOutcomeOutcome.AbsentCase), "absent")]
[JsonDerivedType(typeof(OperationOutcomeOutcome.UnknownCase), "unknown")]
public abstract record OperationOutcomeOutcome
{
    private protected OperationOutcomeOutcome() { }

    /// <summary>The <c>Completed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Receipts.CompletedEffect? Completed => (this as CompletedCase)?.Value;

    public sealed record CompletedCase(Receipts.CompletedEffect Value) : OperationOutcomeOutcome;

    public static implicit operator OperationOutcomeOutcome(Receipts.CompletedEffect value) => new CompletedCase(value);

    /// <summary>The <c>Unsuccessful</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Receipts.UnsuccessfulEffect? Unsuccessful => (this as UnsuccessfulCase)?.Value;

    public sealed record UnsuccessfulCase(Receipts.UnsuccessfulEffect Value) : OperationOutcomeOutcome;

    public static implicit operator OperationOutcomeOutcome(Receipts.UnsuccessfulEffect value) => new UnsuccessfulCase(value);

    /// <summary>The <c>Absent</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Receipts.AbsentEffect? Absent => (this as AbsentCase)?.Value;

    public sealed record AbsentCase(Receipts.AbsentEffect Value) : OperationOutcomeOutcome;

    public static implicit operator OperationOutcomeOutcome(Receipts.AbsentEffect value) => new AbsentCase(value);

    /// <summary>The <c>Unknown</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Receipts.UnknownEffect? Unknown => (this as UnknownCase)?.Value;

    public sealed record UnknownCase(Receipts.UnknownEffect Value) : OperationOutcomeOutcome;

    public static implicit operator OperationOutcomeOutcome(Receipts.UnknownEffect value) => new UnknownCase(value);
}

/// <summary>
/// The native authority generation moved. Observed with or without an epoch;
/// this is the only event whose Event.owner may be absent.
/// </summary>
public sealed record AuthorityChanged
{
    public ulong? Generation { get; init; }

    public ulong? PreviousGeneration { get; init; }

    /// <summary>
    /// NativeControlRevocationReason name, or "acquired".
    /// </summary>
    public string? Reason { get; init; }

    public bool? Active { get; init; }
}

/// <summary>
/// A native rule fired (operations.v1.Rule): journaled before the write it
/// makes. job, radius, actor_id and target_id are the rule's parameters and
/// the pawn and prey it resolved; tick is the game tick of the firing.
/// </summary>
public sealed record RuleFired
{
    public string? RuleId { get; init; }

    public string? Job { get; init; }

    public uint? Radius { get; init; }

    public string? ActorId { get; init; }

    public string? TargetId { get; init; }

    public long? Tick { get; init; }
}

/// <summary>
/// The attached rules' lease passed and native deactivated all of them without
/// Go. One event per attachment.
/// </summary>
public sealed record RuleLeaseExpired
{
    public long? ExpiresAtTick { get; init; }

    public uint? Deactivated { get; init; }
}

public sealed record WatchLatched
{
    public OperationOutcome? Outcome { get; init; }

    public long? TickDeadline { get; init; }
}

public sealed record StopEvent
{
    public StopReason? Reason { get; init; }

    public StopEventEvidence? Evidence { get; init; }

    /// <summary>
    /// The tick at which the supervisor first raised this stop; the event's
    /// context tick is the tick it took (a failed pause retries per frame).
    /// </summary>
    public long? DetectedTick { get; init; }

    /// <summary>
    /// The tick the hazard arose, when the evidence carries one (a new wound's
    /// age); absent otherwise.
    /// </summary>
    public long? OccurrenceTick { get; init; }
}

/// <summary>The <c>evidence</c> choice of <see cref="StopEvent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StopEventEvidence.NotificationsCase), "notifications")]
[JsonDerivedType(typeof(StopEventEvidence.PawnCase), "pawn")]
[JsonDerivedType(typeof(StopEventEvidence.InjuryCase), "injury")]
[JsonDerivedType(typeof(StopEventEvidence.HealthCase), "health")]
[JsonDerivedType(typeof(StopEventEvidence.BudgetCase), "budget")]
[JsonDerivedType(typeof(StopEventEvidence.PauseCase), "pause")]
[JsonDerivedType(typeof(StopEventEvidence.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(StopEventEvidence.WatchCase), "watch")]
[JsonDerivedType(typeof(StopEventEvidence.CombatCase), "combat")]
public abstract record StopEventEvidence
{
    private protected StopEventEvidence() { }

    /// <summary>The <c>Notifications</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public NotificationBatch? Notifications => (this as NotificationsCase)?.Value;

    public sealed record NotificationsCase(NotificationBatch Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(NotificationBatch value) => new NotificationsCase(value);

    /// <summary>The <c>Pawn</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PawnEvent? Pawn => (this as PawnCase)?.Value;

    public sealed record PawnCase(PawnEvent Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(PawnEvent value) => new PawnCase(value);

    /// <summary>The <c>Injury</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Injury? Injury => (this as InjuryCase)?.Value;

    public sealed record InjuryCase(Injury Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(Injury value) => new InjuryCase(value);

    /// <summary>The <c>Health</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public HealthThreshold? Health => (this as HealthCase)?.Value;

    public sealed record HealthCase(HealthThreshold Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(HealthThreshold value) => new HealthCase(value);

    /// <summary>The <c>Budget</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public BudgetReached? Budget => (this as BudgetCase)?.Value;

    public sealed record BudgetCase(BudgetReached Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(BudgetReached value) => new BudgetCase(value);

    /// <summary>The <c>Pause</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PauseEvidence? Pause => (this as PauseCase)?.Value;

    public sealed record PauseCase(PauseEvidence Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(PauseEvidence value) => new PauseCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Watch</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public WatchLatched? Watch => (this as WatchCase)?.Value;

    public sealed record WatchCase(WatchLatched Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(WatchLatched value) => new WatchCase(value);

    /// <summary>The <c>Combat</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CombatEventStop? Combat => (this as CombatCase)?.Value;

    public sealed record CombatCase(CombatEventStop Value) : StopEventEvidence;

    public static implicit operator StopEventEvidence(CombatEventStop value) => new CombatCase(value);
}

/// <summary>
/// STOP_REASON_COMBAT_EVENT evidence: the armed event, the thing it
/// happened to or was raised by (a pawn, a building, a lord's first pawn),
/// and a diagnostic. occurrence_tick is the tick it happened on.
/// </summary>
public sealed record CombatEventStop
{
    public CombatEvent? Event { get; init; }

    public string? ThingId { get; init; }

    public string? Reason { get; init; }
}

public sealed record EpochStarted
{
    public Epoch? Epoch { get; init; }
}

/// <summary>
/// Native noticed the named fact families change under a running epoch
/// without a controller write or a stop (a research project finished, faction
/// relations moved, a zone edited): a controller holding cached rows of those
/// families discards them. Never empty. When native can attribute the change
/// it narrows the discard: entity_ids (&lt;= 64) name the changed rows of the
/// families (a zone's ListZones id) and cells is the one rectangle the change
/// touched, so a store keeps the rest of each family. Without either the
/// whole family is stale.
/// </summary>
public sealed record ObservationInvalidated
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<FactFamily>))]
    public EquatableArray<FactFamily> Families { get; init; }

    public string? Reason { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> EntityIds { get; init; }

    public Rectangle? Cells { get; init; }
}

/// <summary>
/// Inclusive cell bounds, minimum &lt;= maximum on both axes.
/// </summary>
public sealed record Rectangle
{
    public Common.Cell? Minimum { get; init; }

    public Common.Cell? Maximum { get; init; }
}

/// <summary>
/// The epoch's speed, ceiling or regulator changed; the epoch continues. An
/// owner's change carries its new ceiling; a regulator transition carries the
/// ceiling it now holds (zero once it releases) and the blind ticks that
/// drove it.
/// </summary>
public sealed record SpeedChanged
{
    public Speed? Speed { get; init; }

    public uint? MaxTicksPerSecond { get; init; }

    public uint? RegulatedTicksPerSecond { get; init; }

    public long? BlindTicks { get; init; }
}

public sealed record PauseFailed
{
    public StopEvent? Pending { get; init; }
}

public sealed record ForcePauseWaiting
{
    public PauseEvidence? Pause { get; init; }

    public ulong? WaitedMs { get; init; }

    public uint? GraceMs { get; init; }
}

public sealed record ForcePauseCleared
{
    public ulong? WaitedMs { get; init; }

    public ForcePauseKind? ForcePauseKind { get; init; }

    public bool? SpeedRestored { get; init; }
}

public sealed record ClockEvent
{
    public long? Cursor { get; init; }

    /// <summary>
    /// Absent only for the epoch-less events: AuthorityChanged observed outside
    /// any epoch, RuleFired and RuleLeaseExpired (rules run with or without one).
    /// </summary>
    public EpochOwner? Owner { get; init; }

    public Common.ObservationContext? Context { get; init; }

    public long? ObservedAtUnixMs { get; init; }

    public string? Detail { get; init; }

    public ClockEventEvent? Event { get; init; }

    /// <summary>
    /// How long ago, on native's own clock, this event was observed when the
    /// page carrying it was composed: the unobserved time a stop sat in native
    /// before this reply, measured without comparing clocks across processes.
    /// </summary>
    public ulong? AgeAtReplyMs { get; init; }
}

/// <summary>The <c>event</c> choice of <see cref="ClockEvent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClockEventEvent.StartedCase), "started")]
[JsonDerivedType(typeof(ClockEventEvent.SpeedChangedCase), "speedChanged")]
[JsonDerivedType(typeof(ClockEventEvent.StoppedCase), "stopped")]
[JsonDerivedType(typeof(ClockEventEvent.NotificationCase), "notification")]
[JsonDerivedType(typeof(ClockEventEvent.AlertCase), "alert")]
[JsonDerivedType(typeof(ClockEventEvent.InjuryObservedCase), "injuryObserved")]
[JsonDerivedType(typeof(ClockEventEvent.HostilesClearedCase), "hostilesCleared")]
[JsonDerivedType(typeof(ClockEventEvent.PauseFailedCase), "pauseFailed")]
[JsonDerivedType(typeof(ClockEventEvent.ForcePauseWaitingCase), "forcePauseWaiting")]
[JsonDerivedType(typeof(ClockEventEvent.ForcePauseClearedCase), "forcePauseCleared")]
[JsonDerivedType(typeof(ClockEventEvent.OperationOutcomeCase), "operationOutcome")]
[JsonDerivedType(typeof(ClockEventEvent.AuthorityChangedCase), "authorityChanged")]
[JsonDerivedType(typeof(ClockEventEvent.ObservationInvalidatedCase), "observationInvalidated")]
[JsonDerivedType(typeof(ClockEventEvent.RuleFiredCase), "ruleFired")]
[JsonDerivedType(typeof(ClockEventEvent.RuleLeaseExpiredCase), "ruleLeaseExpired")]
public abstract record ClockEventEvent
{
    private protected ClockEventEvent() { }

    /// <summary>The <c>Started</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public EpochStarted? Started => (this as StartedCase)?.Value;

    public sealed record StartedCase(EpochStarted Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(EpochStarted value) => new StartedCase(value);

    /// <summary>The <c>SpeedChanged</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SpeedChanged? SpeedChanged => (this as SpeedChangedCase)?.Value;

    public sealed record SpeedChangedCase(SpeedChanged Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(SpeedChanged value) => new SpeedChangedCase(value);

    /// <summary>The <c>Stopped</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public StopEvent? Stopped => (this as StoppedCase)?.Value;

    public sealed record StoppedCase(StopEvent Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(StopEvent value) => new StoppedCase(value);

    /// <summary>The <c>Notification</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Notification? Notification => (this as NotificationCase)?.Value;

    public sealed record NotificationCase(Notification Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(Notification value) => new NotificationCase(value);

    /// <summary>The <c>Alert</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Alert? Alert => (this as AlertCase)?.Value;

    public sealed record AlertCase(Alert Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(Alert value) => new AlertCase(value);

    /// <summary>The <c>InjuryObserved</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Injury? InjuryObserved => (this as InjuryObservedCase)?.Value;

    public sealed record InjuryObservedCase(Injury Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(Injury value) => new InjuryObservedCase(value);

    /// <summary>The <c>HostilesCleared</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public HostilesCleared? HostilesCleared => (this as HostilesClearedCase)?.Value;

    public sealed record HostilesClearedCase(HostilesCleared Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(HostilesCleared value) => new HostilesClearedCase(value);

    /// <summary>The <c>PauseFailed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PauseFailed? PauseFailed => (this as PauseFailedCase)?.Value;

    public sealed record PauseFailedCase(PauseFailed Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(PauseFailed value) => new PauseFailedCase(value);

    /// <summary>The <c>ForcePauseWaiting</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ForcePauseWaiting? ForcePauseWaiting => (this as ForcePauseWaitingCase)?.Value;

    public sealed record ForcePauseWaitingCase(ForcePauseWaiting Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(ForcePauseWaiting value) => new ForcePauseWaitingCase(value);

    /// <summary>The <c>ForcePauseCleared</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ForcePauseCleared? ForcePauseCleared => (this as ForcePauseClearedCase)?.Value;

    public sealed record ForcePauseClearedCase(ForcePauseCleared Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(ForcePauseCleared value) => new ForcePauseClearedCase(value);

    /// <summary>The <c>OperationOutcome</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public OperationOutcome? OperationOutcome => (this as OperationOutcomeCase)?.Value;

    public sealed record OperationOutcomeCase(OperationOutcome Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(OperationOutcome value) => new OperationOutcomeCase(value);

    /// <summary>The <c>AuthorityChanged</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public AuthorityChanged? AuthorityChanged => (this as AuthorityChangedCase)?.Value;

    public sealed record AuthorityChangedCase(AuthorityChanged Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(AuthorityChanged value) => new AuthorityChangedCase(value);

    /// <summary>The <c>ObservationInvalidated</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public ObservationInvalidated? ObservationInvalidated => (this as ObservationInvalidatedCase)?.Value;

    public sealed record ObservationInvalidatedCase(ObservationInvalidated Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(ObservationInvalidated value) => new ObservationInvalidatedCase(value);

    /// <summary>The <c>RuleFired</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RuleFired? RuleFired => (this as RuleFiredCase)?.Value;

    public sealed record RuleFiredCase(RuleFired Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(RuleFired value) => new RuleFiredCase(value);

    /// <summary>The <c>RuleLeaseExpired</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public RuleLeaseExpired? RuleLeaseExpired => (this as RuleLeaseExpiredCase)?.Value;

    public sealed record RuleLeaseExpiredCase(RuleLeaseExpired Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(RuleLeaseExpired value) => new RuleLeaseExpiredCase(value);
}

public sealed record EventsRequest
{
    public Common.Identity? Identity { get; init; }

    public long? AfterCursor { get; init; }

    public uint? Limit { get; init; }
}

public sealed record EventsPage
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ClockEvent>))]
    public EquatableArray<ClockEvent> Events { get; init; }

    public long? OldestCursor { get; init; }

    public long? NewestCursor { get; init; }

    public long? NextCursor { get; init; }

    public bool? Gap { get; init; }

    public ulong? LostCount { get; init; }
}

public sealed record EventsReply
{
    public EventsReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="EventsReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EventsReplyOutcome.PageCase), "page")]
[JsonDerivedType(typeof(EventsReplyOutcome.FailureCase), "failure")]
public abstract record EventsReplyOutcome
{
    private protected EventsReplyOutcome() { }

    /// <summary>The <c>Page</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public EventsPage? Page => (this as PageCase)?.Value;

    public sealed record PageCase(EventsPage Value) : EventsReplyOutcome;

    public static implicit operator EventsReplyOutcome(EventsPage value) => new PageCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : EventsReplyOutcome;

    public static implicit operator EventsReplyOutcome(Common.Failure value) => new FailureCase(value);
}

