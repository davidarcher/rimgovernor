using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Clock;

/// <summary>
/// Requested speeds. Ultrafast is an ordinary wire speed: native drives the
/// tick boost behind it up to the one ceiling, with no launch gate.
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
/// What bounds the running clock's tick rate right now: the one ceiling
/// (heartbeat or frame budget), else the speed's own rate (FIXED).
/// </summary>
[JsonConverter(typeof(WireEnumConverter<PacingReason>))]
public enum PacingReason
{
    [WireName("PACING_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("PACING_REASON_FIXED")]
    Fixed = 1,
    [WireName("PACING_REASON_FRAME_BUDGET")]
    FrameBudget = 3,
    /// <summary>
    /// The one ceiling is held at Normal (rising back) because the brain has not
    /// sent a heartbeat within the tolerance.
    /// </summary>
    [WireName("PACING_REASON_HEARTBEAT")]
    Heartbeat = 7,
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

/// <summary>The bot-on clock is Running (target speed) or Held (paused for a named reason).</summary>
[JsonConverter(typeof(WireEnumConverter<ClockState>))]
public enum ClockState
{
    [WireName("CLOCK_STATE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("CLOCK_STATE_RUNNING")]
    Running = 1,
    [WireName("CLOCK_STATE_HELD")]
    Held = 2,
}

/// <summary>
/// Why the clock is held. Native raises the hazard, game and error reasons by
/// itself and stays held until the brain sends Run; the brain asks for
/// CoupledOrder, Review and Autosave through Hold. NeverRun is the state
/// before any Run of the loaded game.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<HoldReason>))]
public enum HoldReason
{
    [WireName("HOLD_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("HOLD_REASON_NEVER_RUN")]
    NeverRun = 1,
    [WireName("HOLD_REASON_COUPLED_ORDER")]
    CoupledOrder = 2,
    [WireName("HOLD_REASON_REVIEW")]
    Review = 3,
    [WireName("HOLD_REASON_AUTOSAVE")]
    Autosave = 4,
    /// <summary>The game holds the clock itself (a force-pausing window or a long event that outlived the grace).</summary>
    [WireName("HOLD_REASON_GAME_FORCED")]
    GameForced = 5,
    [WireName("HOLD_REASON_SESSION_CHANGED")]
    SessionChanged = 6,
    [WireName("HOLD_REASON_UNAVAILABLE")]
    Unavailable = 7,
    [WireName("HOLD_REASON_EXTERNAL_PAUSE")]
    ExternalPause = 8,
    [WireName("HOLD_REASON_LETTER_PAUSE")]
    LetterPause = 9,
    [WireName("HOLD_REASON_EXTERNAL_SPEED_CHANGED")]
    ExternalSpeedChanged = 10,
    [WireName("HOLD_REASON_WATCHER_ERROR")]
    WatcherError = 11,
    [WireName("HOLD_REASON_EVENT_JOURNAL_ERROR")]
    EventJournalError = 12,
    [WireName("HOLD_REASON_COLONY_NAMING")]
    ColonyNaming = 13,
    /// <summary>A force-pausing Verse.Dialog_NodeTree the game opened by itself; PauseEvidence.dialog names it.</summary>
    [WireName("HOLD_REASON_DIALOG_PAUSE")]
    DialogPause = 14,
    /// <summary>The requested speed did not take.</summary>
    [WireName("HOLD_REASON_RUN_REFUSED")]
    RunRefused = 15,
    [WireName("HOLD_REASON_NOTIFICATION_BATCH")]
    NotificationBatch = 16,
    [WireName("HOLD_REASON_MEDICAL_REST_CHANGED")]
    MedicalRestChanged = 17,
    [WireName("HOLD_REASON_HOSTILE")]
    Hostile = 18,
    [WireName("HOLD_REASON_COLONIST_DOWNED")]
    ColonistDowned = 19,
    [WireName("HOLD_REASON_PREDATOR_HUNT")]
    PredatorHunt = 20,
    [WireName("HOLD_REASON_HUNTING_ROUTE_UNSAFE")]
    HuntingRouteUnsafe = 21,
    [WireName("HOLD_REASON_COLONIST_HEALTH")]
    ColonistHealth = 22,
    [WireName("HOLD_REASON_COLONIST_INJURY")]
    ColonistInjury = 23,
    /// <summary>An armed combat event (WatchPolicy.combat_stop_events) happened; the clock held at the boundary of the tick it happened on.</summary>
    [WireName("HOLD_REASON_COMBAT_EVENT")]
    CombatEvent = 25,
}

/// <summary>
/// The combat events a WATCH_MODE_COMBAT run can be armed to hold on.
/// Native hooks see each one on the tick it happens and hold the
/// clock at that tick's boundary. Aim warmup, shots and ordinary damage are
/// never holds.
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
    /// WATCH_MODE_COMBAT only: the combat events that hold the clock on the
    /// tick they happen (HOLD_REASON_COMBAT_EVENT). Distinct, specified;
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
    /// A new wound keeps the hold when it bleeds the colonist out within
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
/// Run the clock at the target speed under the watch policy. Idempotent: while
/// running it changes the speed and the policy in place, while held it resumes
/// (and re-baselines the hazard watches). A game-forced pause refuses it.
/// </summary>
public sealed record RunRequest
{
    public Common.Identity? Identity { get; init; }

    /// <summary>The native authority generation the brain holds; a stale one is refused.</summary>
    public ulong? ExpectedGeneration { get; init; }

    public Speed? Speed { get; init; }

    public WatchPolicy? Policy { get; init; }
}

/// <summary>
/// Hold the clock for a brain reason (coupled order, review, autosave). Needs only
/// the loaded identity, never current authority, so cleanup can always stop play.
/// While already held the first reason stands.
/// </summary>
public sealed record HoldRequest
{
    public Common.Identity? Identity { get; init; }

    public HoldReason? Reason { get; init; }

    /// <summary>Short free-text detail for the journal.</summary>
    public string? Detail { get; init; }
}

/// <summary>Brain contact for the one ceiling's heartbeat input. Identity only; reads are not contact.</summary>
public sealed record HeartbeatRequest
{
    public Common.Identity? Identity { get; init; }
}

public sealed record StatusRequest
{
    public Common.Identity? Identity { get; init; }
}

public sealed record Status
{
    public Common.ObservationContext? Context { get; init; }

    /// <summary>Running or Held; unset only when <see cref="Unavailable"/> is set.</summary>
    public ClockState? State { get; init; }

    /// <summary>Why the clock is held; NeverRun before any Run of the loaded game.</summary>
    public HoldReason? Reason { get; init; }

    public string? Detail { get; init; }

    /// <summary>The speed the last Run asked for.</summary>
    public Speed? TargetSpeed { get; init; }

    /// <summary>
    /// The tick rate the running clock holds right now (60 x the multiplier the
    /// game ticks at), and what bounds it.
    /// </summary>
    public uint? PacedTicksPerSecond { get; init; }

    public PacingReason? PacingReason { get; init; }

    /// <summary>
    /// The one ceiling on the tick rate (frame budget and brain heartbeat, the
    /// lower of the two); zero when neither limits it. When it bounds the rate,
    /// pacing_reason names the input that binds it.
    /// </summary>
    public uint? CeilingTicksPerSecond { get; init; }

    /// <summary>Held only: the hold's pause took (the original hold, not an everlasting guarantee).</summary>
    public bool? PauseVerified { get; init; }

    public long? HeldAtUnixMs { get; init; }

    /// <summary>HOLD_REASON_COMBAT_EVENT only: which armed event held the clock.</summary>
    public CombatEvent? CombatEvent { get; init; }

    /// <summary>The status could not be read completely.</summary>
    public Common.Unavailable? Unavailable { get; init; }

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
    /// The game was launched with the acceptance test-acceleration flag; native
    /// accounting only, Run(Ultrafast) is not gated by it.
    /// </summary>
    public bool? TestAccelerationAvailable { get; init; }

    /// <summary>
    /// Wall time the supervisor's own hold/run transitions account for in
    /// this game session, on a monotonic clock: paused_ms is the time held
    /// (the open gap included while held), running_ms the time run.
    /// Both reset when the loaded game changes.
    /// </summary>
    public ulong? PausedMs { get; init; }

    public ulong? RunningMs { get; init; }

    /// <summary>
    /// Supervisor probe accounting, cumulative for the loaded game
    /// session like paused_ms: wall time the hazard probe (probe_elapsed_ms,
    /// probe_total passes) and the fact-change digests (digest_elapsed_ms,
    /// digest_total passes) took on the main thread, and the widest tick gap
    /// between consecutive probes the session saw.
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
    /// The frame account, cumulative for the loaded game session: frames that
    /// ticked, those whose tick work exceeded the frame budget, and the
    /// widest frame's tick work. A command queued for the main thread waits at most one
    /// frame's tick work.
    /// </summary>
    public ulong? PacedFrames { get; init; }

    public ulong? PacedFramesOverBudget { get; init; }

    public double? MaxPacedFrameMs { get; init; }
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

/// <summary>The reply of Status, Run and Hold: the clock status or a failure that had no clock effect.</summary>
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

/// <summary>What a heartbeat answers: the clock state, why it is held, and the ceiling.</summary>
public sealed record HeartbeatAck
{
    public ClockState? State { get; init; }

    public HoldReason? Reason { get; init; }

    public uint? CeilingTicksPerSecond { get; init; }
}

public sealed record HeartbeatReply
{
    public HeartbeatReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="HeartbeatReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HeartbeatReplyOutcome.AckCase), "ack")]
[JsonDerivedType(typeof(HeartbeatReplyOutcome.FailureCase), "failure")]
public abstract record HeartbeatReplyOutcome
{
    private protected HeartbeatReplyOutcome() { }

    /// <summary>The <c>Ack</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public HeartbeatAck? Ack => (this as AckCase)?.Value;

    public sealed record AckCase(HeartbeatAck Value) : HeartbeatReplyOutcome;

    public static implicit operator HeartbeatReplyOutcome(HeartbeatAck value) => new AckCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : HeartbeatReplyOutcome;

    public static implicit operator HeartbeatReplyOutcome(Common.Failure value) => new FailureCase(value);
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

    /// <summary>The last hostile fell on the first probe after a resume, so the count before comes from before the hold.</summary>
    public bool? AcrossResume { get; init; }
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
/// The native authority generation moved. Observed whether the clock runs or is held.
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
/// The clock entered a hold: native raised a hazard, game or error reason, or
/// the brain's Hold took. Journalled once per entry; a Hold while already held
/// keeps the first reason and journals nothing.
/// </summary>
public sealed record HoldEntered
{
    public HoldReason? Reason { get; init; }

    public HoldEvidence? Evidence { get; init; }

    /// <summary>
    /// The tick at which the supervisor first raised this hold; the event's
    /// context tick is the tick it took (a failed pause retries per frame).
    /// </summary>
    public long? DetectedTick { get; init; }

    /// <summary>
    /// The tick the hazard arose, when the evidence carries one (a new wound's
    /// age); absent otherwise.
    /// </summary>
    public long? OccurrenceTick { get; init; }
}

/// <summary>The <c>evidence</c> choice of <see cref="HoldEntered"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HoldEvidence.NotificationsCase), "notifications")]
[JsonDerivedType(typeof(HoldEvidence.PawnCase), "pawn")]
[JsonDerivedType(typeof(HoldEvidence.InjuryCase), "injury")]
[JsonDerivedType(typeof(HoldEvidence.HealthCase), "health")]
[JsonDerivedType(typeof(HoldEvidence.PauseCase), "pause")]
[JsonDerivedType(typeof(HoldEvidence.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(HoldEvidence.CombatCase), "combat")]
public abstract record HoldEvidence
{
    private protected HoldEvidence() { }

    /// <summary>The <c>Notifications</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public NotificationBatch? Notifications => (this as NotificationsCase)?.Value;

    public sealed record NotificationsCase(NotificationBatch Value) : HoldEvidence;

    public static implicit operator HoldEvidence(NotificationBatch value) => new NotificationsCase(value);

    /// <summary>The <c>Pawn</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PawnEvent? Pawn => (this as PawnCase)?.Value;

    public sealed record PawnCase(PawnEvent Value) : HoldEvidence;

    public static implicit operator HoldEvidence(PawnEvent value) => new PawnCase(value);

    /// <summary>The <c>Injury</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Injury? Injury => (this as InjuryCase)?.Value;

    public sealed record InjuryCase(Injury Value) : HoldEvidence;

    public static implicit operator HoldEvidence(Injury value) => new InjuryCase(value);

    /// <summary>The <c>Health</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public HealthThreshold? Health => (this as HealthCase)?.Value;

    public sealed record HealthCase(HealthThreshold Value) : HoldEvidence;

    public static implicit operator HoldEvidence(HealthThreshold value) => new HealthCase(value);

    /// <summary>The <c>Pause</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PauseEvidence? Pause => (this as PauseCase)?.Value;

    public sealed record PauseCase(PauseEvidence Value) : HoldEvidence;

    public static implicit operator HoldEvidence(PauseEvidence value) => new PauseCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : HoldEvidence;

    public static implicit operator HoldEvidence(Common.Unavailable value) => new UnavailableCase(value);

    /// <summary>The <c>Combat</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public CombatEventHold? Combat => (this as CombatCase)?.Value;

    public sealed record CombatCase(CombatEventHold Value) : HoldEvidence;

    public static implicit operator HoldEvidence(CombatEventHold value) => new CombatCase(value);
}

/// <summary>
/// HOLD_REASON_COMBAT_EVENT evidence: the armed event, the thing it
/// happened to or was raised by (a pawn, a building, a lord's first pawn),
/// and a diagnostic. occurrence_tick is the tick it happened on.
/// </summary>
public sealed record CombatEventHold
{
    public CombatEvent? Event { get; init; }

    public string? ThingId { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// The clock resumed: a Run took while held (the first Run of a loaded game included).
/// </summary>
public sealed record Resumed
{
    public Speed? Speed { get; init; }

    /// <summary>The reason the clock was held before this Run.</summary>
    public HoldReason? From { get; init; }
}

/// <summary>
/// Native noticed the named fact families change under a running clock
/// without a controller write or a hold (a research project finished, faction
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

public sealed record PauseFailed
{
    public HoldEntered? Pending { get; init; }
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

    public Common.ObservationContext? Context { get; init; }

    public long? ObservedAtUnixMs { get; init; }

    public string? Detail { get; init; }

    public ClockEventEvent? Event { get; init; }

    /// <summary>
    /// How long ago, on native's own clock, this event was observed when the
    /// page carrying it was composed: the unobserved time a hold sat in native
    /// before this reply, measured without comparing clocks across processes.
    /// </summary>
    public ulong? AgeAtReplyMs { get; init; }
}

/// <summary>The <c>event</c> choice of <see cref="ClockEvent"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClockEventEvent.ResumedCase), "resumed")]
[JsonDerivedType(typeof(ClockEventEvent.HoldEnteredCase), "holdEntered")]
[JsonDerivedType(typeof(ClockEventEvent.NotificationCase), "notification")]
[JsonDerivedType(typeof(ClockEventEvent.AlertCase), "alert")]
[JsonDerivedType(typeof(ClockEventEvent.InjuryObservedCase), "injuryObserved")]
[JsonDerivedType(typeof(ClockEventEvent.HostilesClearedCase), "hostilesCleared")]
[JsonDerivedType(typeof(ClockEventEvent.PauseFailedCase), "pauseFailed")]
[JsonDerivedType(typeof(ClockEventEvent.ForcePauseWaitingCase), "forcePauseWaiting")]
[JsonDerivedType(typeof(ClockEventEvent.ForcePauseClearedCase), "forcePauseCleared")]
[JsonDerivedType(typeof(ClockEventEvent.AuthorityChangedCase), "authorityChanged")]
[JsonDerivedType(typeof(ClockEventEvent.ObservationInvalidatedCase), "observationInvalidated")]
public abstract record ClockEventEvent
{
    private protected ClockEventEvent() { }

    /// <summary>The <c>Resumed</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Resumed? Resumed => (this as ResumedCase)?.Value;

    public sealed record ResumedCase(Resumed Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(Resumed value) => new ResumedCase(value);

    /// <summary>The <c>HoldEntered</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public HoldEntered? HoldEntered => (this as HoldEnteredCase)?.Value;

    public sealed record HoldEnteredCase(HoldEntered Value) : ClockEventEvent;

    public static implicit operator ClockEventEvent(HoldEntered value) => new HoldEnteredCase(value);

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
