namespace RimGovernor.Brain.Policy;

/// <summary>
/// One standing tier turret's service state from the power census. Every field is an observation; an unknown
/// fuel state is neither a deficit nor a rearm.
/// </summary>
public sealed record DefenseTurretFacts
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    /// <summary>The turret's observed damage per second.</summary>
    public Fact<double> Dps { get; init; }

    public Fact<bool> Powered { get; init; }

    public Fact<bool> OutOfFuel { get; init; }

    /// <summary>The definitions the barrel accepts (steel for the mini turret), in the census's order.</summary>
    public EquatableArray<Resource> FuelDefinitions { get; init; }
}

/// <summary>One refuel order for an empty barrel: the pawn that carries it and the fuel in stock it will load.</summary>
public sealed record DefenseRearm(string Turret, Cell Cell, PawnId Pawn, Resource Fuel);

/// <summary>
/// The turret tier's observed service deficits once every tier stands. Unpowered and Empty are the deficit
/// cells; Rearm is the one order proposed for the first empty barrel whose fuel is in stock.
/// </summary>
public sealed record DefenseTurretUpkeep
{
    public EquatableArray<Cell> Unpowered { get; init; }

    public EquatableArray<Cell> Empty { get; init; }

    public EquatableArray<DefenseRearm> Rearm { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Measures the tier's turrets. The rearm's pawn is an available colonist whose Hauling work is not
    /// disabled, preferring one with it enabled at the highest priority. Under a solar flare (blackout) every
    /// turret is dark for the outage and none is a power deficit; an empty barrel is still rearmed.
    /// </summary>
    public static DefenseTurretUpkeep DefenseRearmTurrets(IReadOnlyList<DefenseTurretFacts> turrets, IReadOnlyList<WorkPawn> workers, Fact<IReadOnlyDictionary<Resource, long>> stock, bool blackout)
    {
        var unpowered = new List<Cell>();
        var empty = new List<Cell>();
        var rearm = new List<DefenseRearm>();
        var stockKnown = stock.TryGetValue(out var stocked);
        var pawnOk = DefenseRearmPawn(workers, out var pawn);
        foreach (var t in turrets)
        {
            if (t.Powered.TryGetValue(out var on) && !on && !blackout)
            {
                unpowered.Add(t.Cell);
            }
            if (!t.OutOfFuel.TryGetValue(out var isEmpty) || !isEmpty)
            {
                continue;
            }
            empty.Add(t.Cell);
            if (!stockKnown)
            {
                continue;
            }
            var fuel = new Resource("");
            foreach (var d in t.FuelDefinitions)
            {
                if (stocked!.TryGetValue(d, out var n) && n > 0)
                {
                    fuel = d;
                    break;
                }
            }
            if (fuel.Value == "")
            {
                continue;
            }
            if (pawnOk && rearm.Count == 0 && t.Id != "")
            {
                rearm.Add(new DefenseRearm(t.Id, t.Cell, pawn, fuel));
            }
        }
        return new DefenseTurretUpkeep
        {
            Unpowered = EquatableArray.From(unpowered),
            Empty = EquatableArray.From(empty),
            Rearm = EquatableArray.From(rearm),
        };
    }

    private static bool DefenseRearmPawn(IReadOnlyList<WorkPawn> workers, out PawnId best)
    {
        best = default;
        var bestPriority = 0;
        var found = false;
        foreach (var w in workers)
        {
            if (!w.Available.TryGetValue(out var available) || !available
                || !w.Applies.TryGetValue(out var applies) || !applies
                || !w.Work.TryGetValue(out var work))
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
