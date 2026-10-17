using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.NativeFacade;

// The colony events native pushes to the brain (ColonyEventChannel) and writes as OTLP rows (otel-signals.md). Plain
// in-process records, built directly by their native producers: nothing serializes them, so they carry no wire shape,
// no optional-everything fields and no validation. They reuse only the wire HoldReason, CombatEvent, FactFamily and
// Speed enums.

/// <summary>Where and when native observed an event: the loaded world, its game tick, and native's Unix ms.</summary>
public sealed record ColonyEventOrigin(string ColonyId, string LoadToken, int MapId, long Tick, long ObservedAtUnixMs);

/// <summary>One colony event: its origin and a human-readable detail.</summary>
public abstract record ColonyEvent(ColonyEventOrigin Origin, string Detail);

/// <summary>
/// The clock entered a hold: native raised a hazard, game or error reason, or the brain's Hold took. Published once per
/// entry; a Hold while already held keeps the first reason and publishes nothing.
/// </summary>
/// <param name="DetectedTick">The tick the hold was first raised at; the origin tick is the tick it took (a failed pause retries per frame).</param>
/// <param name="OccurrenceTick">The tick the hazard arose, when the evidence carries one (a new wound's age).</param>
/// <param name="StopAtUnixMs">Native's Unix ms when the hold took: the stamp Status reports as held-at.</param>
public sealed record HoldEntered(ColonyEventOrigin Origin, string Detail, Clock.HoldReason Reason, HoldEvidence Evidence,
    long DetectedTick, long? OccurrenceTick, long StopAtUnixMs) : ColonyEvent(Origin, Detail);

/// <summary>A Run took while held (the first Run of a loaded game included).</summary>
/// <param name="From">The reason the clock was held before this Run.</param>
public sealed record Resumed(ColonyEventOrigin Origin, string Detail, Clock.Speed Speed, Clock.HoldReason From) : ColonyEvent(Origin, Detail);

/// <summary>The native authority generation moved; observed whether the clock runs or is held.</summary>
/// <param name="Reason">The NativeControlRevocationReason name, or "acquired".</param>
public sealed record AuthorityChanged(ColonyEventOrigin Origin, string Detail, ulong Generation, string Reason, bool Active) : ColonyEvent(Origin, Detail);

/// <summary>A new high-priority game alert. An alert never holds the clock.</summary>
/// <param name="Key">The stable key, type|priority|normalized-label.</param>
public sealed record AlertRaised(ColonyEventOrigin Origin, string Detail, string Key, string Label, string? Priority) : ColonyEvent(Origin, Detail);

/// <summary>A new informational letter that did not hold the clock.</summary>
public sealed record LetterNotified(ColonyEventOrigin Origin, string Detail, EventLetter Letter) : ColonyEvent(Origin, Detail);

/// <summary>A new informational transient message that did not hold the clock.</summary>
public sealed record MessageNotified(ColonyEventOrigin Origin, string Detail, string Id, string Text) : ColonyEvent(Origin, Detail);

/// <summary>A colonist injury observed without a hold (suppressed, demoted or sub-threshold).</summary>
public sealed record InjuryObserved(ColonyEventOrigin Origin, string Detail, EventInjury Injury) : ColonyEvent(Origin, Detail);

/// <summary>The last conscious hostile went down or died.</summary>
public sealed record HostilesCleared(ColonyEventOrigin Origin, string Detail) : ColonyEvent(Origin, Detail);

/// <summary>
/// Native noticed the named fact families change under a running clock without a controller write or a hold: a
/// controller holding cached rows of those families discards them. Never empty. When native can attribute the change
/// it narrows the discard: <paramref name="EntityIds"/> (at most InvalidationEntitiesMax) name the changed rows and
/// <paramref name="Cells"/> is the one rectangle the change touched. Without either the whole family is stale.
/// </summary>
public sealed record ObservationInvalidated(ColonyEventOrigin Origin, string Detail, IReadOnlyList<Clock.FactFamily> Families, string Reason,
    IReadOnlyList<string> EntityIds, CellRectangle? Cells) : ColonyEvent(Origin, Detail);

/// <summary>A long event or a windowless force pause began; native waits it out without holding.</summary>
public sealed record ForcePauseWaiting(ColonyEventOrigin Origin, string Detail) : ColonyEvent(Origin, Detail);

/// <summary>The long event or windowless force pause cleared and the requested speed was restored.</summary>
public sealed record ForcePauseCleared(ColonyEventOrigin Origin, string Detail) : ColonyEvent(Origin, Detail);

/// <summary>A pending hold's pause did not take; the hold stays armed and native retries each frame.</summary>
public sealed record PauseFailed(ColonyEventOrigin Origin, string Detail, Clock.HoldReason Pending) : ColonyEvent(Origin, Detail);

/// <summary>What a hold carries beyond its reason.</summary>
public abstract record HoldEvidence;

/// <summary>The new stopping letters of a notification batch.</summary>
public sealed record NotificationEvidence(IReadOnlyList<EventLetter> Letters) : HoldEvidence;

/// <summary>The pawn a hostile, downed or predator hold names.</summary>
public sealed record PawnEvidence(EventPawn Pawn) : HoldEvidence;

/// <summary>A colonist injury hold.</summary>
public sealed record InjuryEvidence(EventInjury Injury) : HoldEvidence;

/// <summary>A combat health threshold crossing, with the policy fractions it crossed.</summary>
public sealed record HealthEvidence(EventPawn Pawn, float MinHealthFraction, float HealthDropFraction) : HoldEvidence;

/// <summary>
/// The pause itself: whether the game is paused and the hold's pause took, the letter vanilla paused for (a letter
/// pause) and the force-pausing dialog's title (a dialog pause).
/// </summary>
public sealed record PauseEvidence(bool ActualPaused, bool PauseVerified, EventLetter? Letter, string? DialogTitle) : HoldEvidence;

/// <summary>An armed combat event: the event, the thing it happened to or was raised by, and a diagnostic.</summary>
public sealed record CombatEvidence(Clock.CombatEvent Event, string? ThingId, string Reason) : HoldEvidence;

/// <summary>The hold belongs to a game that is no longer loaded, so nothing beyond its reason and detail was observed.</summary>
public sealed record UnobservedEvidence : HoldEvidence;

/// <summary>A pawn an event names, at its cell; <paramref name="Reason"/> is the hazard's diagnostic where one applies.</summary>
public sealed record EventPawn(string PawnId, string Name, int X, int Z, string? Reason);

/// <summary>A colonist's summed injury severity before and after the change that raised the event.</summary>
public sealed record EventInjury(EventPawn Pawn, float SeverityBefore, float SeverityAfter);

/// <summary>A letter; <paramref name="Label"/> is null when it left the stack before the event was built.</summary>
public sealed record EventLetter(string Id, string? Label, string? DefName);

/// <summary>Inclusive cell bounds, minimum &lt;= maximum on both axes.</summary>
public sealed record CellRectangle(int MinX, int MinZ, int MaxX, int MaxZ);
