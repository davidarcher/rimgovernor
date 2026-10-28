using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>The journal's current draft requirements, also rechecked when an undraft dispatches.</summary>
    public Task<HashSet<PawnId>> PlannedDraftsAsync(CancellationToken ct) => RunAsync(PlannedDraftsIn, ct);

    private static HashSet<PawnId> PlannedDraftsIn(StoreTransaction tx)
    {
        var needed = new HashSet<PawnId>();
        foreach (var plan in LoadPlansIn(tx))
        {
            var open = WorkProgress.Open(plan.Progress);
            foreach (var progress in plan.Progress)
            {
                var view = progress.View;
                if (progress.Action.OwnedDraft is { } draft &&
                    (view.Unresolved || view.Stage is Stage.Pending or Stage.Prepared or Stage.Dispatched or Stage.AwaitingObservation
                        || DraftOwnershipRules.PlanHoldsDraft(plan.Progress, view)))
                    needed.Add(draft.Pawn);
                if (progress.Action.Capture is { } capture && open) needed.Add(capture.Capturer);
            }
        }
        foreach (var fight in tx.Query("SELECT open,memory,roster FROM combat_fights WHERE open=1", [],
            r => DecodeCombatFight(r.GetInt64(0), r.GetString(1), r.GetString(2))))
            needed.UnionWith(fight.Roster);
        return needed;
    }
}
