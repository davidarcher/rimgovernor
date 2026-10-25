using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>Whether accepted and outstanding pawn orders still require an owned draft.</summary>
public static class DraftOwnershipRules
{
    /// <summary>
    /// Whether the plan still holds the draft v, either completed or still in flight (dispatched, receipt
    /// accepted, awaiting the observation a hold interrupted): no other action of the plan has failed or been
    /// cancelled, and at least one is still unfinished. An order riding on another draft (a move or a subdue
    /// naming a different draft action) says nothing about this one: a timed-out move for one defender must not
    /// undraft the others mid-order. An order riding on this draft that native is still executing holds it
    /// outright: a recovered goal cancels the plan's unissued orders (settleUnissuedWork) and leaves the
    /// dispatched ones to close on their own, which they cannot once the pawn is undrafted under them. A
    /// completed subdue order stands on its draft the same way: its intent receipt is terminal while native
    /// keeps fighting, so the draft is held until the planner cancels the order (releaseBreakWork, once the
    /// victim is downed or out of the break).
    /// </summary>
    public static bool PlanHoldsDraft(IReadOnlyList<Progress> progress, ProgressView v)
    {
        if (v.Stage is not (Stage.Completed or Stage.Dispatched or Stage.AwaitingObservation))
        {
            return false;
        }
        var unfinished = false;
        var settled = false;
        foreach (var p in progress)
        {
            var other = p.View;
            if (other.Action == v.Action)
            {
                continue;
            }
            var rides = OrderDraft(p.Action);
            if (rides is { } r && r != v.Action)
            {
                continue;
            }
            switch (other.Stage)
            {
                case Stage.Cancelled or Stage.Unsuccessful:
                    settled = true;
                    break;
                case Stage.Dispatched or Stage.AwaitingObservation:
                    if (rides == v.Action)
                    {
                        return true;
                    }
                    unfinished = true;
                    break;
                case Stage.Pending or Stage.Prepared:
                    unfinished = true;
                    break;
                case Stage.Completed:
                    if (rides == v.Action && p.Action.Kind == ActionKind.Subdue)
                    {
                        return true;
                    }
                    break;
            }
        }
        return unfinished && !settled;
    }

    /// <summary>Names the owned draft an order rides on, or null for an action that is not a drafted order.</summary>
    public static ActionId? OrderDraft(DomainAction action)
    {
        if (action.Movement is { } m)
        {
            return m.DraftAction;
        }
        if (action.Subdue is { } s)
        {
            return s.DraftAction;
        }
        return null;
    }
}
