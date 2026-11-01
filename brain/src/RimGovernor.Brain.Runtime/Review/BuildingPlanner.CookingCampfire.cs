using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>The MethodId retiring the building <paramref name="id"/> under <paramref name="prefix"/>: one Method per building.</summary>
    internal static MethodId RetireMethodId(string prefix, string id)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return new MethodId($"{prefix}-{Convert.ToHexStringLower(sum.AsSpan(0, 8))}");
    }

    /// <summary>
    /// Takes one standing building down through an excavation-style admission, once per building: uninstalled to storage when
    /// the transition choice stores it (<paramref name="uninstall"/>), deconstructed otherwise.
    /// </summary>
    internal async Task<BuildingResult> RetireBuildingAsync(ControlState state, ReviewStep review, NeedOwner goal, ColonyReading reading, string id, string def, Cell cell, bool uninstall, string prefix, string reason, CancellationToken call, CancellationToken epoch)
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
        var action = uninstall
            ? DomainAction.NewUninstallBuildingAction(new ActionId($"{snapshot.Plan}-0"), new MoveBuilding(id, def, cell, Rotation.South))
            : DomainAction.NewDeconstructionAction(new ActionId($"{snapshot.Plan}-0"), new Deconstruction(id, def, cell));
        var spec = new PlanSpec(snapshot.Plan, new PlanRevision(1), [action]);
        Tel.Decide(new Decision
        {
            Kind = TelemetryKinds.LayoutEditKind,
            Component = "building",
            Verdict = "admitted",
            Reason = uninstall ? "building_uninstall" : "building_retire",
            Target = id,
            Attrs = new Dictionary<string, object?> { ["family"] = "building", ["owner"] = goal.OwnerKey, ["x"] = cell.X, ["z"] = cell.Z },
        });
        var facts = reading;
        return await AdmitExcavationAsync(new ExcavationStep { State = state, Review = review, Owner = goal, Facts = facts }, snapshot, method, spec, null, new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick }, Check, call, epoch).ConfigureAwait(false);
    }
}
