using System.Globalization;

namespace RimGovernor.Brain.Runtime;

public sealed partial class BuildingPlanner
{
    /// <summary>Commits the auto-refuel switch of a heat campfire as one owner method.</summary>
    internal BuildingResult CommitTemperatureControl(NeedOwner goal, Action check, CancellationToken call)
    {
        var p = Reviewer.Player;
        var proposal = Temperature;
        if (proposal is { Method: Pol.TemperatureMethod.SetTarget })
            return CommitTemperatureTarget(goal, proposal.Key, proposal.Thing, proposal.TargetC, check, call);
        if (proposal is null || (proposal.Method != Pol.TemperatureMethod.RefuelOff && proposal.Method != Pol.TemperatureMethod.RefuelOn))
        {
            throw new ControlException("commitCampfireRefuel: not a refuel proposal");
        }
        if (p.Journal.OwnerMethodPlan(goal, proposal.Key, call) is not null)
        {
            return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "campfire_refuel_method") };
        }
        var value = new AutoRefuel(proposal.Thing, proposal.Method == Pol.TemperatureMethod.RefuelOn);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewAutoRefuelAction(new ActionId($"{id}-0"), value);
        var spec = new PlanSpec(id, new PlanRevision(1), [action]);
        check();
        return new BuildingResult { Verdict = OwnerCommitVerdict(p.Journal.CommitOwnerWork(goal, proposal.Key, "", spec, call)) };
    }

    internal async Task<BuildingResult> RetireCampfireAsync(ControlState state, ReviewStep review, NeedOwner goal, ColonyReading reading, CurrentBuilding campfire, CancellationToken call, CancellationToken epoch) =>
        await RetireBuildingAsync(state, review, goal, reading, campfire.Id, (campfire.Building ?? throw new ControlException("campfire retire: current building carries no building")).Definition, campfire.Cells[0], "campfire-retire", "building_retire_method", call, epoch).ConfigureAwait(false);

    /// <summary>Deconstructs one standing building through an excavation-style admission, once per building.</summary>
    internal async Task<BuildingResult> RetireBuildingAsync(ControlState state, ReviewStep review, NeedOwner goal, ColonyReading reading, string id, string def, Cell cell, string prefix, string reason, CancellationToken call, CancellationToken epoch)
    {
        if (reading.Facts.Preferences.Protects(id))
        {
            return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, "protected_thing") };
        }
        var p = Reviewer.Player;
        var method = RetireMethodId(prefix, id);
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return new BuildingResult { Verdict = WaitFor(Cause.MethodUsed, reason) };
        }
        var snapshot = state.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };

        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("retireBuilding: p.session.State() != state");
            }
        }

        Check();
        var value = new Deconstruction(id, def, cell);
        var action = DomainAction.NewDeconstructionAction(new ActionId($"{snapshot.Plan}-0"), value);
        var spec = new PlanSpec(snapshot.Plan, new PlanRevision(1), [action]);
        Tel.Decide(new Decision
        {
            Kind = TelemetryKinds.LayoutEditKind,
            Component = "building",
            Verdict = "admitted",
            Reason = "building_retire",
            Target = id,
            Attrs = new Dictionary<string, object?> { ["family"] = "building", ["owner"] = goal.OwnerKey, ["x"] = cell.X, ["z"] = cell.Z },
        });
        var facts = reading;
        return await AdmitExcavationAsync(new ExcavationStep { State = state, Review = review, Owner = goal, Facts = facts }, snapshot, method, spec, null, new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick }, Check, call, epoch).ConfigureAwait(false);
    }
}
