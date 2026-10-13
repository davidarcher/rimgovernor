using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefensePlanner
{
    /// <summary>Commits the permit and psycast calls of a combat step as one incident method.</summary>
    internal void CommitPermitCalls(CancellationToken call, CancellationToken epoch, IncidentState incident, IReadOnlyList<CombatOrder> calls)
    {
        if (calls.Count == 0)
        {
            return;
        }
        var id = PlanIds.MintPlanId();
        var actions = PermitCallActions(id, calls);
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        var hash = new StringBuilder();
        foreach (var c in calls)
        {
            hash.Append(CultureInfo.InvariantCulture, $"{c.Pawn.Value}/{c.Faction}/{c.Permit}/{c.Target.Value}/{c.Cell.X},{c.Cell.Z}\n");
        }
        var method = DefenseMethodId("permit", incident.Methods.Count, hash);
        var p = _reviewer.Player;
        p.Current(call, epoch);
        p.Journal.CommitIncidentMethod(incident.Incident.Id, method, "", spec, call);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Splits the permit and psycast calls from the other orders of a combat decision.</summary>
    internal static (List<CombatOrder> Others, List<CombatOrder> Calls) SplitPermitCalls(IReadOnlyList<CombatOrder> orders)
    {
        var rest = new List<CombatOrder>();
        var calls = new List<CombatOrder>();
        foreach (var order in orders)
        {
            if (order.Kind == PolicyApi.OrderPermit || order.Kind == PolicyApi.OrderCast)
            {
                calls.Add(order);
            }
            else
            {
                rest.Add(order);
            }
        }
        return (rest, calls);
    }

    internal static List<DomainAction> PermitCallActions(PlanId plan, IReadOnlyList<CombatOrder> calls)
    {
        var actions = new List<DomainAction>(calls.Count);
        for (var i = 0; i < calls.Count; i++)
        {
            var (source, target) = CallAbility(calls[i]);
            var ability = new Ability(calls[i].Pawn, source, target);
            actions.Add(DomainAction.NewAbilityAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{plan}-p{i}")), ability));
        }
        return actions;
    }

    internal static (AbilitySource Source, AbilityTarget Target) CallAbility(CombatOrder call)
    {
        if (call.Kind != PolicyApi.OrderCast)
        {
            return (AbilitySource.PermitSource(call.Faction, call.Permit), AbilityTarget.AbilityCellTarget(call.Cell));
        }
        var source = AbilitySource.PsycastSource(call.Permit);
        if (call.Arm == PolicyApi.PsycastTargetSelf)
        {
            return (source, AbilityTarget.NoAbilityTarget());
        }
        if (call.Arm == PolicyApi.PsycastTargetPawn)
        {
            return (source, AbilityTarget.AbilityPawnTarget(call.Target));
        }
        if (call.Arm == PolicyApi.PsycastTargetCell)
        {
            return (source, AbilityTarget.AbilityCellTarget(call.Cell));
        }
        throw new DomainException($"psycast cast of {call.Permit} has no target arm");
    }

    /// <summary>The memory the decision produced with the permit and cast marks of the memory it started from.</summary>
    internal static CombatMemory WithoutPermitMarks(CombatMemory next, CombatMemory memory)
    {
        next.Permitted = [.. memory.Permitted];
        next.Casts = [.. memory.Casts];
        return next;
    }
}
