namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsFoodStorageUpkeepPlanner
{
    /// <summary>Admits the allow, forbid or haul action of the corpse larder choice as one method.</summary>
    internal RoundsFoodStorageUpkeepResult AdmitCorpseLarder(StepArbiter arbiter, StandardState goal, O.ColonyFactsSnapshot observed, CorpseLarderMethod choice, CancellationToken ct, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        var started = Reviewer.Clock.Now();
        var stock = choice.Stock ?? throw new ControlException("admitCorpseLarder: choice carries no stock");
        var handling = choice.Handling ?? throw new ControlException("admitCorpseLarder: choice carries no handling");
        var method = new MethodId($"corpse-larder-{choice.Kind}-{stock.Id}-{goal.Revision}");
        var id = PlanIds.MintPlanId();
        var actionId = new ActionId($"{id}-0");
        DomainAction action;
        switch (choice.Kind)
        {
            case "allow":
                action = DomainAction.NewSupplyAllowAction(actionId, new SupplyAllow(stock.Id, stock.DefName.Value, handling.Cell));
                break;
            case "forbid":
                action = DomainAction.NewSupplyAllowAction(actionId, SupplyAllow.NewSupplyForbid(stock.Id, stock.DefName.Value, handling.Cell));
                break;
            case "haul":
                if (!arbiter.TryClaim([handling.Hauler], "haul-item:" + stock.Id))
                {
                    return new RoundsFoodStorageUpkeepResult { Verdict = WaitFor(Cause.MethodUsed, "haul_item_claim") };
                }
                action = DomainAction.NewHaulAction(actionId, new Haul((Domain.PawnId)handling.Hauler, stock.Id, stock.DefName.Value, handling.Cell));
                break;
            default:
                throw new ControlException($"admitCorpseLarder: kind \"{choice.Kind}\"");
        }
        var spec = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(ct, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("admitCorpseLarder: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, spec, ct);
        return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
