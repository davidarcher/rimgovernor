namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one reader of usable resource stock: the item census, with WoodLog taken from the wood count the latch
/// reads so a floor and the latch agree.
/// </summary>
public readonly record struct StockReader(EquatableArray<Amount> Resources, long Wood)
{
    /// <summary>A reader over a census whose WoodLog rows are the wood count.</summary>
    public static StockReader Of(EquatableArray<Amount> census) =>
        new(census, census.Where(row => row.Resource == ConstructionDemandRules.ConstructionWoodLog).Sum(row => row.Count));

    /// <summary>Resource's usable units; WoodLog is the wood count.</summary>
    public long Count(Resource resource)
    {
        if (resource == ConstructionDemandRules.ConstructionWoodLog)
        {
            return Wood;
        }
        long n = 0;
        foreach (var a in Resources)
        {
            if (a.Resource == resource)
            {
                n += a.Count;
            }
        }
        return n;
    }

    /// <summary>The census with its WoodLog row taken from the wood count.</summary>
    public EquatableArray<Amount> Census()
    {
        var result = new List<Amount>(Resources.Where(row => row.Resource != ConstructionDemandRules.ConstructionWoodLog))
        {
            new Amount { Resource = ConstructionDemandRules.ConstructionWoodLog, Count = Wood },
        };
        return EquatableArray.From(result);
    }
}

/// <summary>
/// One still-open action of an admitted method and the
/// units of Resource its preview costs.
/// </summary>
public sealed record AdmittedCost
{
    public Resource Resource { get; init; }

    public ActionId Action { get; init; }

    public long Count { get; init; }
}

/// <summary>Every source of construction material demand.</summary>
public sealed record ConstructionDemandInput
{
    public StockReader Stock { get; init; }

    /// <summary>The material standing blueprints and frames are still owed.</summary>
    /// <summary>Null while the census is unread.</summary>
    public IReadOnlyDictionary<Resource, long>? Owed { get; init; }

    /// <summary>The open costs of admitted-but-unplaced methods.</summary>
    public EquatableArray<AdmittedCost> Admitted { get; init; }

    /// <summary>The material the whole layout plan still owes (PlannedShellDemand), before any room is admitted.</summary>
    public IReadOnlyDictionary<Resource, long>? Planned { get; init; }
}

public static class ConstructionDemandRules
{
    public static readonly Resource ConstructionWoodLog = new("WoodLog");

    /// <summary>
    /// The stock the economy/resource Need must reach for construction, per resource: the plan's unbuilt walls and doors, a blueprint's owed material, the
    /// open costs of admitted methods (each action once), each counted where the stock falls short of it, merged by maximum. It is an absolute stock level;
    /// the supply plan measures its shortfall against the stock of the moment.
    /// </summary>
    public static Dictionary<Resource, long> ConstructionDemand(ConstructionDemandInput input)
    {
        var result = new Dictionary<Resource, long>();
        void Raise(Resource resource, long need)
        {
            if (need <= 0 || need <= result.GetValueOrDefault(resource))
            {
                return;
            }
            result[resource] = need;
        }
        bool Short(Resource resource, long need) => need > input.Stock.Count(resource);
        if (input.Owed is { } owed)
        {
            foreach (var (resource, n) in owed)
            {
                if (Short(resource, n))
                {
                    Raise(resource, n);
                }
            }
        }
        if (input.Planned is { } planned)
        {
            foreach (var (resource, n) in planned)
            {
                if (Short(resource, n))
                {
                    Raise(resource, n);
                }
            }
        }
        foreach (var (resource, need) in AdmittedNeeds(input.Admitted))
        {
            if (Short(resource, need))
            {
                Raise(resource, need);
            }
        }
        return result;
    }

    /// <summary>The open cost of admitted methods per resource, each action once.</summary>
    internal static Dictionary<Resource, long> AdmittedNeeds(IReadOnlyList<AdmittedCost> admitted)
    {
        var costs = new Dictionary<Resource, Dictionary<ActionId, long>>();
        foreach (var c in admitted)
        {
            if (c.Count <= 0 || c.Resource.Value.Length == 0)
            {
                continue;
            }
            if (!costs.TryGetValue(c.Resource, out var actions))
            {
                actions = [];
                costs[c.Resource] = actions;
            }
            actions[c.Action] = Math.Max(actions.GetValueOrDefault(c.Action), c.Count);
        }
        var result = new Dictionary<Resource, long>(costs.Count);
        foreach (var (resource, actions) in costs)
        {
            result[resource] = actions.Values.Sum();
        }
        return result;
    }

    /// <summary>ConstructionDemand over a review's facts.</summary>
    public static Dictionary<Resource, long> ConstructionDemandOf(RoundsFacts f, RoundsPolicy p, RoundsLatches l) =>
        ConstructionDemand(new ConstructionDemandInput
        {
            Stock = new StockReader(f.Resources, f.Wood),
            Owed = f.CurrentConstruction?.Deficit,
            Admitted = f.Admitted,
            Planned = f.PlannedConstruction is { } rooms ? PlannedRules.PlannedTotal(rooms.Values) : null,
        });
}
