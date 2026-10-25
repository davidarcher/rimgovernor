using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// The per-family admission checks every owner shares, then the plan itself; the caller then writes the
    /// methods row. A refusal on the current evidence is returned, with nothing written; a malformed plan throws.
    /// </summary>
    internal static AdmissionRefusal? AdmitOwnerMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (FamilyAdmissionRules.UnsupportedReason(owner.Need, plan) is { } unsupported)
            return new(RefusalKind.SharedAdmission, unsupported);
        var review = LoadReview(tx);
        var current = review.Enabled && review.Snapshot == owner.Snapshot;
        if (FamilyAdmissionRules.BillConflict(owner.Need, plan, current) is { } bill) throw new ConflictException(bill);
        if (FamilyAdmissionRules.ZoneConflict(owner.Need, plan, current) is { } zone) throw new ConflictException(zone);
        if (FamilyAdmissionRules.AcquisitionConflict(owner.Need, plan, current) is { } acquisition) throw new ConflictException(acquisition);
        if (FamilyAdmissionRules.WorkConflict(owner.Need, plan, current) is { } work) throw new ConflictException(work);
        if (FamilyAdmissionRules.SupplyConflict(owner.Need, plan, current, review.LarderSupplies, review.ReserveSupplies,
            review.EventLoot.Pending, review.PollutionSupplies) is { } supply) throw new ConflictException(supply);
        if (WorkResourcesRefusalIn(tx, owner, plan) is { } refused)
        {
            return refused;
        }
        CreatePlanIn(tx, plan);
        return null;
    }

    /// <summary>
    /// The tail every owner's commit shares: refuse a method the owner already binds in its epoch, run the family
    /// admission and store the plan, then write the methods row and name the plan's method. An admission refusal is
    /// returned with nothing written.
    /// </summary>
    internal static AdmissionRefusal? BindOwnerMethod(StoreTransaction tx, NeedOwner owner, MethodId method, string reason, PlanSpec plan)
    {
        // Plan ids are minted; the real double-admission key is the owner table's primary key (owner, [episode,] method).
        if (owner.BoundPlan(tx, method) is { } bound)
        {
            throw new MethodBoundException($"{owner.OwnerLabel} already binds method {method.Value} to plan {bound.Value}");
        }
        // Native execution reports the payload's Method. It must identify the journal owner that
        // authorized it, and a plan must not race multiple revisions of one pawn through Hands.
        var activityPawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in plan.Actions)
        {
            if (action.PawnActivities is not { } batch) continue;
            batch.Validate();
            foreach (var order in batch.Orders)
            {
                if (order.Method != method.Value)
                    throw new ConflictException("activity order method does not match its journal method");
                if (!activityPawns.Add(order.Pawn))
                    throw new ConflictException("method repeats a pawn across activity actions");
            }
        }
        if (AdmitOwnerMethod(tx, owner, plan) is { } refused)
        {
            return refused;
        }
        owner.BindMethod(tx, method, plan.Id, reason);
        _ = tx.Execute("UPDATE plans SET method_id=$method WHERE id=$id", ("$method", method), ("$id", plan.Id));
        return null;
    }

    /// <summary>
    /// Method admission's backstop for the Safeguards the planner already asked: a vetoed proposal is refused with
    /// the Safeguard's reason. An owner the review does not bind (a player goal) is outside the routine Safeguards.
    /// </summary>
    internal static AdmissionRefusal? SafeguardRefusal(StoreTransaction tx, NeedOwner owner)
    {
        var review = LoadReview(tx);
        var (refusal, vetoed) = review.Refuse(new SafeguardProposal());
        return vetoed ? new(RefusalKind.SharedAdmission, $"{owner.OwnerLabel}: {refusal.Safeguard}: {refusal.Reason}") : null;
    }

    /// <summary>
    /// The Safeguard backstop of the paths outside claimed commits (combat commits, dispatch, activity continuation),
    /// which still report a veto as <see cref="NotAdmittedException"/>.
    /// </summary>
    internal static void AdmitReviewSafeguards(StoreTransaction tx, NeedOwner owner)
    {
        if (SafeguardRefusal(tx, owner) is { } refused)
        {
            throw new NotAdmittedException(refused.Reason);
        }
    }

    /// <summary><see cref="BindOwnerMethod"/> for the combat commits outside claimed commits: a refusal is <see cref="NotAdmittedException"/>.</summary>
    internal static void BindOwnerMethodOrThrow(StoreTransaction tx, NeedOwner owner, MethodId method, string reason, PlanSpec plan)
    {
        if (BindOwnerMethod(tx, owner, method, reason, plan) is { } refused)
        {
            throw new NotAdmittedException(refused.Reason);
        }
    }

}
