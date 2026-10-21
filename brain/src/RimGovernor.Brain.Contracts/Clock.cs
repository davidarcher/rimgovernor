using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Clock;

/// <summary>
/// Requested speeds, all ordinary wire speeds with no launch gate. Ultrafast is
/// the game's own 15x with the tick boost forced off; Hyperfast drives the tick
/// boost under its own 30x cap (1800 ticks/s). The one ceiling applies beneath both.
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
    [WireName("SPEED_HYPERFAST")]
    Hyperfast = 5,
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
    /// <summary>The review tick the Run named was reached (native's cadence hold), or the bot stopped acting (a brain hold).</summary>
    [WireName("HOLD_REASON_REVIEW")]
    Review = 3,
    [WireName("HOLD_REASON_AUTOSAVE")]
    Autosave = 4,
    /// <summary>The game holds the clock itself (a force-pausing window or a long event that outlived the grace).</summary>
    [WireName("HOLD_REASON_GAME_FORCED")]
    GameForced = 5,
    [WireName("HOLD_REASON_SESSION_CHANGED")]
    SessionChanged = 6,
    /// <summary>The clock itself is unavailable: the tick manager, the native hooks or the authority behind the epoch are gone. Nothing else (a pawn activity, a job) raises it.</summary>
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
    /// <summary>
    /// Colony mode: a colonist not in WatchPolicy.acknowledged_hungry_colonist_ids reached
    /// WatchPolicy.hunger_hold_category, so the brain reviews their food under a held clock.
    /// </summary>
    [WireName("HOLD_REASON_COLONIST_NEED")]
    ColonistNeed = 26,
    /// <summary>
    /// The colony is lost: no free colonist is left on the clock's map or anywhere else the player has one (another map,
    /// a caravan, a travelling transporter), or the game's own GameEnder is ending the game. Terminal: the brain never
    /// sends Run on it, never opens the game-over letter and never creates new wanderers (#2819).
    /// </summary>
    [WireName("HOLD_REASON_COLONY_LOST")]
    ColonyLost = 27,
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
    /// Required: colony mode holds (HOLD_REASON_COLONIST_NEED) when a colonist's food need reaches this hunger
    /// category (Need_Food.CurCategory, the game's own per-pawn thresholds) and they are not acknowledged. Hungry,
    /// UrgentlyHungry or Starving; Fed is refused. A combat watch never holds for hunger.
    /// </summary>
    public Defs.HungerCategory? HungerHoldCategory { get; init; }

    /// <summary>
    /// Colonists already at or past <see cref="HungerHoldCategory"/> when the brain last reviewed them under a held
    /// clock; native holds for none of them until they leave this list.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AcknowledgedHungryColonistIds { get; init; }

    /// <summary>
    /// WATCH_MODE_COMBAT only: the combat events that hold the clock on the
    /// tick they happen (HOLD_REASON_COMBAT_EVENT). Distinct, specified;
    /// empty arms none. Refused outside combat mode.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CombatEvent>))]
    public EquatableArray<CombatEvent> CombatStopEvents { get; init; }

    /// <summary>
    /// Hazard thresholds; all required, each finite and positive.
    /// One blow of at least serious_single_hit_damage, summary health crossing
    /// under serious_summary_health_floor (also the floor a resting patient must
    /// stay over to remain eligible), a total bleed rate crossing over
    /// serious_bleed_rate_floor, or a vital part hit under serious_vital_part_floor
    /// of its health is a serious injury. An explosive landing within its blast
    /// radius plus explosive_near_margin_cells of a colonist is near them;
    /// melee_reach_cells is the range assumed for a pawn with no ranged weapon.
    /// A new wound keeps the hold when it bleeds the colonist out within
    /// injury_severity_floor_ticks or is life-threatening.
    /// </summary>
    public float? SeriousSingleHitDamage { get; init; }

    public float? SeriousSummaryHealthFloor { get; init; }

    public float? SeriousBleedRateFloor { get; init; }

    public float? SeriousVitalPartFloor { get; init; }

    public float? ExplosiveNearMarginCells { get; init; }

    public float? MeleeReachCells { get; init; }

    public uint? InjurySeverityFloorTicks { get; init; }
}

/// <summary>
/// Run the clock at the target speed under the watch policy until <see cref="HoldAtTick"/>. Idempotent: while
/// running it changes the speed, the policy and the hold tick in place, while held it resumes
/// (and re-baselines the hazard watches). A game-forced pause refuses it.
/// </summary>
public sealed record RunRequest
{
    public Common.Identity? Identity { get; init; }

    /// <summary>The native authority generation the brain holds; a stale one is refused.</summary>
    public ulong? ExpectedGeneration { get; init; }

    public Speed? Speed { get; init; }

    public WatchPolicy? Policy { get; init; }

    /// <summary>
    /// The <see cref="Status.HoldSerial"/> the step that planned this Run read. Resuming a held clock is refused
    /// (Unavailable; the hold stays) when native has entered a hold since, so a hold the step did not see is never
    /// released.
    /// </summary>
    public ulong? PlannedHoldSerial { get; init; }

    /// <summary>
    /// The game tick native holds the clock at for <see cref="HoldReason.Review"/>: the brain's next review. Required.
    /// A tick the clock has already reached holds at the next tick (an early review). The hold is native's, on the
    /// tick boundary; the brain's step starts from it and ends with the next Run, which names the review after.
    /// </summary>
    public ulong? HoldAtTick { get; init; }
}

/// <summary>
/// Hold the clock for a brain reason (coupled order, autosave, or review when the bot stops acting). Needs only
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
    /// Bumped on every hold entry (a native hazard and the brain's own holds alike), on the game thread. Holds pause
    /// the game, so a tick cannot order a read against a same-tick hold; this serial does. A Run quotes it as
    /// <see cref="RunRequest.PlannedHoldSerial"/>.
    /// </summary>
    public ulong? HoldSerial { get; init; }

    /// <summary>
    /// The last speed the player asked for in game (native's RequestSpeed); unset when none was asked for in the
    /// loaded game. Native never runs at it by itself: the brain adopts it as its target speed and sends a Run.
    /// </summary>
    public Speed? PlayerRequestedSpeed { get; init; }

    /// <summary>
    /// Bumped on every in-game speed request, on the game thread; it starts again with the loaded game. Set exactly
    /// when <see cref="PlayerRequestedSpeed"/> is. The brain adopts a serial it has not adopted yet.
    /// </summary>
    public ulong? PlayerSpeedRequestSerial { get; init; }

    /// <summary>
    /// The one ceiling on the tick rate (frame budget and brain heartbeat, the
    /// lower of the two); zero when neither limits it. When it bounds the rate,
    /// pacing_reason names the input that binds it.
    /// </summary>
    public uint? CeilingTicksPerSecond { get; init; }

    public long? HeldAtUnixMs { get; init; }

    /// <summary>HOLD_REASON_COMBAT_EVENT only: which armed event held the clock.</summary>
    public CombatEvent? CombatEvent { get; init; }

    /// <summary>The status could not be read completely.</summary>
    public Common.Unavailable? Unavailable { get; init; }

    public bool? NativeTickBoundary { get; init; }

    public bool? ActualPaused { get; init; }

    /// <summary>
    /// Game ticks per wall second over the last ten seconds of frames, paused
    /// time included: what the player actually gets.
    /// </summary>
    public double? EffectiveTicksPerSecond { get; init; }
}

/// <summary>The reply of Status, Run and Hold: the clock status or a failure that had no clock effect.</summary>
public sealed record StatusReply
{
    public StatusReplyOutcome Outcome { get; init; } = null!;
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

/// <summary>What a heartbeat answers: contact was recorded.</summary>
public sealed record HeartbeatAck;

public sealed record HeartbeatReply
{
    public HeartbeatReplyOutcome Outcome { get; init; } = null!;
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
