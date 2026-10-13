using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>
/// A stored plan decoded: its spec, the progress each action has replayed to, and its zone footprint records
/// (Go <c>PlanState</c>). Immutable, so the plan cache hands the same instance to every reader.
/// </summary>
/// <param name="Retired">True once the plan left the active catalog; a retired plan is read-only.</param>
/// <param name="SupersededBy">The plan that replaced this one, empty when none.</param>
/// <param name="Method">
/// The goal or incident method id the plan was admitted under, empty for a plan no method binds. It outlives the
/// methods row, which a goal rebuild clears on a world's first round.
/// </param>
/// <param name="Spec">The plan as submitted.</param>
/// <param name="Progress">One entry per action, in action order.</param>
/// <param name="Admissions">The footprint record of each zone action that has one.</param>
public sealed record PlanState(
    bool Retired,
    PlanId SupersededBy,
    MethodId Method,
    PlanSpec Spec,
    EquatableArray<Progress> Progress,
    EquatableArray<ActionAdmission> Admissions
);

/// <summary>
/// A zone's footprint record, written once at method admission (<see cref="Store.AdmitBuildingMethod"/>); the
/// zone_create intent prepares only under it. Building intents carry none: native validates a placement when it
/// applies the intent.
/// </summary>
public sealed record Admission(GenerationSnapshot Snapshot, Tick Tick, EquatableArray<Cell> Footprint);

/// <summary>An <see cref="Admission"/> and the action it covers.</summary>
public sealed record ActionAdmission(ActionId Action, Admission Admission);

/// <summary>A loaded map of a save: the scope control, the root plan and incidents belong to (Go <c>World</c>).</summary>
public readonly record struct World(ColonyId Colony, LoadId Load, MapId Map)
{
    /// <summary>Whether the world's identity is valid.</summary>
    public bool IsValid => new GenerationSnapshot(Colony, Map, Load, new PlanId("world-validation"), new PlanRevision(1), default).IsValid;

    /// <summary>Throws when the world's identity is not valid (Go <c>World.Validate</c>).</summary>
    public void Validate() => new GenerationSnapshot(Colony, Map, Load, new PlanId("world-validation"), new PlanRevision(1), default).Validate();
}

/// <summary>Go <c>BatchAttempt</c>: one action of a prepare or dispatch batch.</summary>
public sealed record BatchAttempt(PlanId Plan, ActionId Action, GenerationSnapshot Snapshot, Tick Tick);

/// <summary>Go <c>BatchReceipt</c>: one receipt of a <see cref="Store.RecordReceipts"/> batch.</summary>
public sealed record BatchReceipt(PlanId Plan, ActionId Action, AttemptId Attempt, Receipt Receipt)
{
    /// <summary>The combat results a combat batch's receipt carries.</summary>
    public EquatableArray<CombatResult> Combat { get; init; }

    /// <summary>Native's account of a refused receipt; null for any other.</summary>
    public NativeRefusal? Refusal { get; init; }
}

/// <summary>
/// One batch item's outcome, in input order: the progress its single call would return, or the error it would
/// throw. A failed item is left out of the batch without aborting the others.
/// </summary>
public sealed record BatchResult(Progress? Progress, Exception? Error);

/// <summary>The plan cache's lifetime counters (Go <c>PlanCacheStats</c>).</summary>
public readonly record struct PlanCacheStats(long Hits, long Misses, long Drops, long Flushes);
