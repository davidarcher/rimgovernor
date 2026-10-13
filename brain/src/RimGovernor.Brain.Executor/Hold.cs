using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using System.Collections.Frozen;

namespace RimGovernor.Brain.Executor;

// Port of internal/executor/hold.go.

public sealed partial class Executor
{
    // Maps every Policy.Reason an Admit-style check can return, the emergency subset and the ordinary admission
    // refusals alike, to its durable HeldReason mirror. A reason absent from this table is a bug: HoldRefusalAsync
    // drops it rather than persisting a wrong one, so the table must stay exhaustive over the declared reasons.
    private static readonly FrozenDictionary<Reason, HeldReason> s_reasonHeldReasons = new Dictionary<Reason, HeldReason>
    {
        [Reason.UnsafeThreat] = HeldReason.UnsafeThreat,
        [Reason.CriticalMedical] = HeldReason.CriticalMedical,
        [Reason.StaleFacts] = HeldReason.StaleFacts,
        [Reason.UnknownFacts] = HeldReason.UnknownFacts,
        [Reason.NotReady] = HeldReason.NotReady,
        [Reason.AlreadyReserved] = HeldReason.AlreadyReserved,
        [Reason.UnsafePlacement] = HeldReason.UnsafePlacement,
        [Reason.MaterialRequired] = HeldReason.MaterialRequired,
        [Reason.DependencyBlocked] = HeldReason.DependencyBlocked,
        [Reason.GeometryBlocked] = HeldReason.GeometryBlocked,
        [Reason.InvalidHeld] = HeldReason.InvalidHeld,
        [Reason.NativeIneligible] = HeldReason.NativeIneligible,
        [PolicyFunctions.ExcavationUnsupported] = HeldReason.ExcavationUnsupported,
        [PolicyFunctions.ExcavationGeometryChanged] = HeldReason.ExcavationGeometryChanged,
        [PolicyFunctions.UnsafeRoute] = HeldReason.UnsafeRoute,
        [PolicyFunctions.RoofSupportRisk] = HeldReason.RoofSupportRisk,
        [PolicyFunctions.StorageMissing] = HeldReason.StorageMissing,
        [PolicyFunctions.UrgentCompetingWork] = HeldReason.UrgentCompetingWork,
    }.ToFrozenDictionary();

    // Durably records a refused-but-not-yet-dispatched action's admission-refusal reasons via the journal's
    // Hold, deduplicated and best effort: a durable-write failure must not mask the caller's HeldException, the
    // action stays Pending or Prepared regardless, and the next inspection recomputes and records the reason
    // again. Shared by every family whose Admit-style check produces refusals.
    private async Task<Progress> HoldRefusalAsync(PlanId plan, ActionId actionId, IEnumerable<Refusal> refused, Tick tick, Progress progress, CancellationToken ct)
    {
        var seen = new HashSet<HeldReason>();
        var reasons = new List<HeldReason>();
        foreach (var refusal in refused)
        {
            if (s_reasonHeldReasons.TryGetValue(refusal.Reason, out var held) && seen.Add(held))
            {
                reasons.Add(held);
            }
        }
        if (reasons.Count == 0)
        {
            return progress;
        }
        try
        {
            return await _journal.HoldAsync(plan, actionId, reasons, tick, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return progress;
        }
    }
}
