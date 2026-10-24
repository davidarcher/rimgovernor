namespace RimGovernor.Brain.Policy;

/// <summary>
/// One standing tier turret's service state from the power census: its gun's damage per second, whether it is
/// powered, and whether it is a refuelable turret out of fuel.
/// </summary>
public sealed record DefenseTurretFacts
{
    public string Id { get; init; } = "";

    public Cell Cell { get; init; }

    /// <summary>The turret's observed damage per second.</summary>
    public required double Dps { get; init; }

    public required bool Powered { get; init; }

    /// <summary>A refuelable turret with an empty barrel; false for one with fuel or one that takes none.</summary>
    public required bool OutOfFuel { get; init; }

    /// <summary>The definitions the barrel accepts (steel for the mini turret), in the census's order.</summary>
    public EquatableArray<Resource> FuelDefinitions { get; init; }
}

/// <summary>
/// The turret tier's observed service deficits once every tier stands. Unpowered and Empty are the deficit
/// cells; Rearm is whether an empty barrel has fuel in stock and an available hauler to carry it.
/// </summary>
public sealed record DefenseTurretUpkeep
{
    public EquatableArray<Cell> Unpowered { get; init; }

    public EquatableArray<Cell> Empty { get; init; }

    public bool Rearm { get; init; }
}

public static class DefenseRearm
{
    /// <summary>
    /// Measures the tier's turrets. The rearm's pawn is an available colonist whose Hauling work is not
    /// disabled, preferring one with it enabled at the highest priority. Under a solar flare (blackout) every
    /// turret is dark for the outage and none is a power deficit; an empty barrel is still rearmed.
    /// </summary>
    public static DefenseTurretUpkeep DefenseRearmTurrets(IReadOnlyList<DefenseTurretFacts> turrets, IReadOnlyList<WorkPawn> workers, IReadOnlyDictionary<Resource, long> stock, bool blackout)
    {
        var unpowered = new List<Cell>();
        var empty = new List<Cell>();
        var rearm = false;
        var pawnOk = DefenseRearmPawn(workers, out _);
        foreach (var t in turrets)
        {
            if (!t.Powered && !blackout)
            {
                unpowered.Add(t.Cell);
            }
            if (!t.OutOfFuel)
            {
                continue;
            }
            empty.Add(t.Cell);
            var fueled = false;
            foreach (var d in t.FuelDefinitions)
            {
                if (stock.TryGetValue(d, out var n) && n > 0)
                {
                    fueled = true;
                    break;
                }
            }
            if (fueled && pawnOk && t.Id != "")
            {
                rearm = true;
            }
        }
        return new DefenseTurretUpkeep
        {
            Unpowered = EquatableArray.From(unpowered),
            Empty = EquatableArray.From(empty),
            Rearm = rearm,
        };
    }

    internal static bool DefenseRearmPawn(IReadOnlyList<WorkPawn> workers, out PawnId best)
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
