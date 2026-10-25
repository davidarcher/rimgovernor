using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Clock;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Clock;

[JsonConverter(typeof(WireEnumConverter<WatchMode>))]
public enum WatchMode
{
    [WireName("WATCH_MODE_COLONY")]
    Colony = 1,
    [WireName("WATCH_MODE_COMBAT")]
    Combat = 2,
}

public sealed record WatchPolicy
{
    public required WatchMode Mode { get; init; }

    public required float HealthDropFraction { get; init; }

    public required float MinHealthFraction { get; init; }

    public required float HostileWithin { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AcknowledgedHostileIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AcknowledgedDownedColonistIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> AcknowledgedInjuredColonistIds { get; init; }

    public required uint InjuryStopCooldownMs { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> SurgicalRecoveryIds { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> MedicalRestIds { get; init; }

    /// <summary>
    /// Required: colony mode holds (HOLD_REASON_COLONIST_NEED) when a colonist's food need reaches this hunger
    /// category (Need_Food.CurCategory, the game's own per-pawn thresholds) and they are not acknowledged. Hungry,
    /// UrgentlyHungry or Starving; Fed is refused. A combat watch never holds for hunger.
    /// </summary>
    public required RimGovernor.Brain.GameFacts.Defs.HungerCategory HungerHoldCategory { get; init; }

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
    public required float SeriousSingleHitDamage { get; init; }

    public required float SeriousSummaryHealthFloor { get; init; }

    public required float SeriousBleedRateFloor { get; init; }

    public required float SeriousVitalPartFloor { get; init; }

    public required float ExplosiveNearMarginCells { get; init; }

    public required float MeleeReachCells { get; init; }

    public required uint InjurySeverityFloorTicks { get; init; }
}

/// <summary>
/// Run the clock at the target speed under the watch policy until <see cref="HoldAtTick"/>. Idempotent: while
/// running it changes the speed, the policy and the hold tick in place, while held it resumes
/// (and re-baselines the hazard watches). A game-forced pause refuses it.
/// </summary>
public sealed record RunRequest
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    /// <summary>The native authority generation the brain holds; a stale one is refused.</summary>
    public required ulong ExpectedGeneration { get; init; }

    public required Speed Speed { get; init; }

    public required WatchPolicy Policy { get; init; }

    /// <summary>
    /// The <see cref="Status.HoldSerial"/> the step that planned this Run read. Resuming a held clock is refused
    /// (Unavailable; the hold stays) when native has entered a hold since, so a hold the step did not see is never
    /// released.
    /// </summary>
    public required ulong PlannedHoldSerial { get; init; }

    /// <summary>
    /// The game tick native holds the clock at for <see cref="HoldReason.Review"/>: the brain's next review. Required.
    /// A tick the clock has already reached holds at the next tick (an early review). The hold is native's, on the
    /// tick boundary; the brain's step starts from it and ends with the next Run, which names the review after.
    /// </summary>
    public required ulong HoldAtTick { get; init; }
}

/// <summary>
/// Hold the clock for a brain reason (coupled order, autosave, or review when the bot stops acting). Needs only
/// the loaded identity, never current authority, so cleanup can always stop play.
/// While already held the first reason stands.
/// </summary>
public sealed record HoldRequest
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    public required HoldReason Reason { get; init; }

    /// <summary>Short free-text detail for the journal.</summary>
    public string? Detail { get; init; }
}

/// <summary>Brain contact for the one ceiling's heartbeat input. Identity only; reads are not contact.</summary>
public sealed record HeartbeatRequest
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }
}

public sealed record StatusRequest
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }
}
