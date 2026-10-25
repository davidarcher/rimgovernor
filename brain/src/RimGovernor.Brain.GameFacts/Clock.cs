using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Clock;

/// <summary>
/// Requested speeds, all ordinary wire speeds with no launch gate. Ultrafast is
/// the game's own 15x with the tick boost forced off; Hyperfast drives the tick
/// boost under its own 30x cap (1800 ticks/s). The one ceiling applies beneath both.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<Speed>))]
public enum Speed
{
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

/// <summary>The bot-on clock is Running (target speed) or Held (paused for a named reason).</summary>
[JsonConverter(typeof(WireEnumConverter<ClockState>))]
public enum ClockState
{
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

public sealed record Status
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }
    public required ClockState State { get; init; }
    public required HoldState Hold { get; init; }
    public required RunHistory LastRun { get; init; }
    public required Pacing Pacing { get; init; }
    public required ulong HoldSerial { get; init; }
    public required PlayerSpeedRequest PlayerRequest { get; init; }
    public required bool NativeTickBoundary { get; init; }
    public required bool ActualPaused { get; init; }
    /// <summary>Game ticks per wall second over the last ten seconds, including pauses.</summary>
    public required double EffectiveTicksPerSecond { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HoldState.RunningCase), "running")]
[JsonDerivedType(typeof(HoldState.NeverRunCase), "neverRun")]
[JsonDerivedType(typeof(HoldState.PendingCase), "pending")]
[JsonDerivedType(typeof(HoldState.HeldCase), "held")]
public abstract record HoldState
{
    private protected HoldState() { }
    public sealed record RunningCase : HoldState;
    public sealed record NeverRunCase : HoldState;
    public sealed record PendingCase([property: JsonRequired] HoldReason Reason) : HoldState;
    public sealed record HeldCase([property: JsonRequired] HoldReason Reason, [property: JsonRequired] long AtUnixMs, [property: JsonRequired] HoldTrigger Trigger) : HoldState;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HoldTrigger.OrdinaryCase), "ordinary")]
[JsonDerivedType(typeof(HoldTrigger.CombatCase), "combat")]
public abstract record HoldTrigger
{
    private protected HoldTrigger() { }
    public sealed record OrdinaryCase : HoldTrigger;
    public sealed record CombatCase([property: JsonRequired] CombatEvent Event) : HoldTrigger;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunHistory.NeverRunCase), "neverRun")]
[JsonDerivedType(typeof(RunHistory.RanCase), "ran")]
public abstract record RunHistory
{
    private protected RunHistory() { }
    public sealed record NeverRunCase : RunHistory;
    public sealed record RanCase([property: JsonRequired] Speed Speed) : RunHistory;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Pacing.StoppedCase), "stopped")]
[JsonDerivedType(typeof(Pacing.RunningCase), "running")]
public abstract record Pacing
{
    private protected Pacing() { }
    public sealed record StoppedCase : Pacing;
    public sealed record RunningCase([property: JsonRequired] uint TicksPerSecond, [property: JsonRequired] PacingReason Reason, [property: JsonRequired] uint CeilingTicksPerSecond) : Pacing;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlayerSpeedRequest.NoneCase), "none")]
[JsonDerivedType(typeof(PlayerSpeedRequest.RequestedCase), "requested")]
public abstract record PlayerSpeedRequest
{
    private protected PlayerSpeedRequest() { }
    public sealed record NoneCase : PlayerSpeedRequest;
    public sealed record RequestedCase([property: JsonRequired] Speed Speed, [property: JsonRequired] ulong Serial) : PlayerSpeedRequest;
}

/// <summary>What a heartbeat answers: contact was recorded.</summary>
public sealed record HeartbeatAck;
