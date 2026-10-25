using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Store.StorePackage;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    internal static void InsertPlanOwner(StoreTransaction tx, PlanId plan) =>
        tx.ExecuteOrConflict("INSERT INTO plan_owner(plan_id,kind) VALUES($plan,'need')", ("$plan", plan));

    /// <summary>Whether any plan of the owner is still open work, or a fight of one holds.</summary>
    internal static bool CombatOpenWork(StoreTransaction tx, NeedOwner owner)
    {
        foreach (var plan in owner.OwnerPlans)
        {
            // A fight's drafts are its open work.
            if (PlanOpen(LoadIn(tx, plan)) || CombatFightHolds(tx, plan)) return true;
        }
        return false;
    }

    /// <summary><see cref="CombatOpenWork"/> without the fights: whether any of the owner's plans is still open.</summary>
    internal static bool PlanOpenWork(StoreTransaction tx, NeedOwner owner) => owner.OwnerPlans.Any(plan => PlanOpen(LoadIn(tx, plan)));

    /// <summary>
    /// The guard every prepare and dispatch runs: the plan is live, its world is not retired, and its owning
    /// Need still admits current work.
    /// </summary>
    internal static void GuardPlanWork(StoreTransaction tx, PlanId plan, GenerationSnapshot current, Tick tick)
    {
        var (retired, found) = tx.QueryFirst("SELECT retired FROM plans WHERE id=$id", [("$id", plan)], r => r.GetInt64(0) != 0);
        if (!found)
        {
            throw new NotFoundException();
        }
        if (retired)
        {
            throw new DomainException("retired plan does not admit work");
        }
        tx.Floors.Guard(current, tick);
        if (ActivityReleaseContinuation(tx, plan, current)) return;
        if (tx.ScalarText("SELECT need_id FROM need_methods WHERE plan_id=$id", ("$id", plan)) is null) return;
        GuardNeedWork(tx, plan, current);
    }

    /// <summary>
    /// The combat standing of an enabled review (#3311): <see cref="Rules.CombatSituation"/> over the
    /// review's hostiles and the food plan's hunt request, filed while it is active or the <c>military/combat</c> Need
    /// still holds open work (a fight's drafts); none otherwise. A clear situation cancels the Need's undispatched
    /// Methods first, so nothing dispatches after the clear.
    /// </summary>
    internal static ReviewCombat? ReviewCombat(StoreTransaction tx, ReviewRequest request)
    {
        var hunt = HuntSquadRules.HuntRequest(request.Facts.FoodPlan);
        var situation = Rules.CombatSituation(request.Facts.Hostiles, hunt.Count);
        var owner = LoadNeedIn(tx, MilitaryFamily.Combat, request.Current);
        if (situation == Situation.Clear)
        {
            CancelUndispatchedMethods(tx, owner);
        }
        if (situation != Situation.Active && !CombatOpenWork(tx, owner))
        {
            return null;
        }
        // The prey is the hunt origin's: only a fight with no hostile standing hunts it.
        var prey = situation == Situation.Active && request.Facts.Hostiles is HostileCensus.Quiet
            ? EquatableArray.From(hunt.Select(p => (PawnId)p))
            : default;
        var hunters = situation == Situation.Active && request.Facts.Hostiles is HostileCensus.Quiet
            ? EquatableArray.From(HuntPlanningRules.HuntRequestHunters(request.Facts.FoodPlan).Select(p => (PawnId)p))
            : default;
        return new ReviewCombat(situation, prey, hunters);
    }

    /// <summary>
    /// Cancels the owner's plans whose every action is still undispatched, so a recovered situation leaves no
    /// work to start. Work already dispatched keeps its own settlement path.
    /// </summary>
    internal static void CancelUndispatchedMethods(StoreTransaction tx, NeedOwner owner)
    {
        foreach (var plan in owner.OwnerPlans)
        {
            var p = LoadIn(tx, plan);
            if (p.Retired || !WorkProgress.Open(p.Progress))
            {
                continue;
            }
            // Recovery cancels new work, never the cleanup needed to surrender an existing assignment.
            if (ActivityReleasePlan(p.Spec)) continue;
            var undispatched = p.Progress.All(progress =>
            {
                var v = progress.View;
                return v.Attempt.Value == 0 && v.Stage is Stage.Pending or Stage.Prepared or Stage.Cancelled;
            });
            if (!undispatched)
            {
                continue;
            }
            foreach (var progress in p.Progress)
            {
                var v = progress.View;
                if (v.Stage != Stage.Cancelled)
                {
                    _ = AdvanceIn(tx, plan, v.Action, new Transition(TransitionKind.Cancel));
                }
            }
        }
    }
}
