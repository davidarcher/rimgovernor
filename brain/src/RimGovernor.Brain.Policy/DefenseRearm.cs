using BuildingRow = RimGovernor.Brain.GameFacts.Observations.BuildingState;

namespace RimGovernor.Brain.Policy;

/// <summary>The turret tier's observed power deficit once every tier stands: the cells of its unpowered turrets.</summary>
public sealed record DefenseTurretUpkeep
{
    public EquatableArray<Cell> Unpowered { get; init; }
}

/// <summary>
/// The defense's turret upkeep (task 7.4): a gun turret whose barrel is spent (its CompRefuelable out of fuel) is rearmed
/// through the maintenance choice's refuel (ServicingChoiceRules over the fuel deficit, task 5.x), as the power family
/// refuels a generator: one exact RecoveryService refuel per chosen pawn, its refusals spending the order's budget and a
/// running native refuel observed rather than reordered. A barrel the choice cannot refuel is named by its decision.
/// </summary>
public static class DefenseRearm
{
    /// <summary>The MethodId prefix of a turret refuel, <c>refuel-&lt;turret&gt;-&lt;pawn&gt;</c>.</summary>
    public const string RefuelMethod = "refuel-";

    /// <summary>The deficit the rearm answers: a spent barrel; a damaged turret is home upkeep's.</summary>
    public static readonly IReadOnlySet<ServicingDeficit> Considered = new HashSet<ServicingDeficit> { ServicingDeficit.Fuel };

    /// <summary>The tier turrets that are a power deficit. Under a solar flare (blackout) every turret is dark for the outage and none is.</summary>
    public static DefenseTurretUpkeep DefenseRearmTurrets(IReadOnlyList<DefenseTurretFacts> turrets, bool blackout) =>
        new() { Unpowered = blackout ? [] : EquatableArray.From(turrets.Where(t => !t.Powered).Select(t => t.Cell)) };

    /// <summary>The standing gun turrets whose barrel is spent, in id order.</summary>
    public static IReadOnlyList<BuildingRow> EmptyTurrets(IEnumerable<BuildingRow> rows) =>
        [.. rows.Where(b => b.Turret is not null && b.Service.Fuel is RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { OutOfFuel: true })
            .OrderBy(b => b.Id, StringComparer.Ordinal)];

    /// <summary>Each spent turret's refuel decision over the servicing census of the same read and the refuels underway and refused.</summary>
    public static IReadOnlyList<ServicingDecision> Refuels(IReadOnlyList<BuildingRow> empty, ServicingCensus? census, IReadOnlyList<ServicingUnderway> underway, IReadOnlyList<RefusedServicing> refused) =>
        empty.Count == 0 ? [] : ServicingChoiceRules.Decide(new ServicingRequest
        {
            Considered = Considered,
            Targets = empty,
            Census = census,
            Unneeded = new HashSet<string>(StringComparer.Ordinal),
            Underway = underway,
            Refused = refused,
        });

    /// <summary>The chosen refuels as exact RecoveryService refuels claiming their turret and pawn; an issued one continues its open Method.</summary>
    public static IReadOnlyList<NeedMethod> Methods(NeedId need, IReadOnlyList<ServicingDecision> decisions)
    {
        var output = new List<NeedMethod>();
        foreach (var d in decisions)
        {
            if (d.Underway is not null || d.Chosen.Kind != ServicingChoiceKind.Refuel)
            {
                continue;
            }
            var pawn = new PawnId(d.Chosen.Pawn);
            var target = d.Target;
            output.Add(new NeedMethod(need, new MethodId(RefuelMethod + target + "-" + d.Chosen.Pawn), ServicingChoiceRules.Describe(d),
                id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewRecoveryServiceAction(new ActionId(id.Value + "-0"), new RecoveryService(pawn, target, RecoveryMethod.Refuel))])));
        }
        return output;
    }

    /// <summary>The available colonist whose Hauling work is enabled at the highest priority (the throne refuel's carrier).</summary>
    internal static bool DefenseRearmPawn(IReadOnlyList<WorkPawn> workers, out PawnId best)
    {
        best = default;
        var bestPriority = 0;
        var found = false;
        foreach (var w in workers)
        {
            if (!w.Available || w.Work is not PawnWorkTable.Works { Rows: var work })
            {
                continue;
            }
            int priority = 0;
            bool disabled = false, listed = false;
            foreach (var p in work)
            {
                if (p.Work == WorkType.Hauling)
                {
                    priority = p.Priority;
                    disabled = p.Disabled;
                    listed = true;
                }
            }
            if (!listed || disabled)
            {
                continue;
            }
            // Enabled hauling (priority 1..4) beats disabled-by-priority (0); among enabled, the lower number is the higher priority.
            var rank = priority > 0 ? 5 - priority : 0;
            if (!found || rank > bestPriority || (rank == bestPriority && w.Id < best))
            {
                best = w.Id;
                bestPriority = rank;
                found = true;
            }
        }
        return found;
    }
}
