namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsFoodStorageUpkeepPlanner
{
    /// <summary>Admits the allow, forbid or haul action of the corpse larder choice as one method.</summary>
    internal RoundsFoodStorageUpkeepResult AdmitCorpseLarder(CancellationToken ct, CancellationToken epoch, StepArbiter arbiter, StandardState goal, O.ColonyFactsSnapshot observed, CorpseLarderMethod choice)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        var started = _reviewer.Clock.Now();
        var method = new MethodId($"corpse-larder-{choice.Kind}-{choice.Stock.Id}-{goal.Revision}");
        var id = PlanIds.MintPlanId();
        var actionId = new ActionId($"{id}-0");
        DomainAction action;
        switch (choice.Kind)
        {
            case "allow":
                action = DomainAction.NewSupplyAllowAction(actionId, new SupplyAllow(choice.Stock.Id, choice.Stock.DefName.Value, choice.Handling.Cell));
                break;
            case "forbid":
                action = DomainAction.NewSupplyAllowAction(actionId, SupplyAllow.NewSupplyForbid(choice.Stock.Id, choice.Stock.DefName.Value, choice.Handling.Cell));
                break;
            case "haul":
                if (!arbiter.TryClaim([choice.Handling.Hauler], "haul-item:" + choice.Stock.Id))
                {
                    return new RoundsFoodStorageUpkeepResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "haul_item_claim") };
                }
                action = DomainAction.NewHaulAction(actionId, new Haul(choice.Handling.Hauler, choice.Stock.Id, choice.Stock.DefName.Value, choice.Handling.Cell));
                break;
            default:
                throw new ControlException($"admitCorpseLarder: kind \"{choice.Kind}\"");
        }
        var spec = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(ct, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("admitCorpseLarder: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, spec, ct);
        return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
