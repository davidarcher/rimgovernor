namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one reader of usable resource stock: the item census, with WoodLog taken from the wood fact the latch
/// reads so a floor and the latch agree.
/// </summary>
public readonly record struct StockReader(Fact<EquatableArray<Amount>> Resources, Fact<long> Wood)
{
    /// <summary>Resource's usable units, unknown when the census (or, for WoodLog without a wood fact, the census) is unknown.</summary>
    public Fact<long> Count(Resource resource)
    {
        if (resource == PolicyFunctions.ConstructionWoodLog && Wood.TryGetValue(out var wood))
        {
            return Known(wood);
        }
        if (!Resources.TryGetValue(out var rows))
        {
            return Unknown<long>();
        }
        long n = 0;
        foreach (var a in rows)
        {
            if (a.Resource == resource)
            {
                n += a.Count;
            }
        }
        return Known(n);
    }

    /// <summary>Count with an unknown census read as zero.</summary>
    public long Units(Resource resource) => Count(resource).TryGetValue(out var n) ? n : 0;

    /// <summary>The census with its WoodLog row taken from the wood fact. An unknown census stays unknown unless wood is the only target.</summary>
    public Fact<EquatableArray<Amount>> Census(IReadOnlyDictionary<Resource, long> targets)
    {
        if (!Wood.TryGetValue(out var n))
        {
            return Resources;
        }
        var known = Resources.TryGetValue(out var rows);
        if (!known && !(targets.ContainsKey(PolicyFunctions.ConstructionWoodLog) && targets.Count == 1))
        {
            return Resources;
        }
        var result = new List<Amount>();
        if (known)
        {
            result.AddRange(rows.Where(row => row.Resource != PolicyFunctions.ConstructionWoodLog));
        }
        result.Add(new Amount { Resource = PolicyFunctions.ConstructionWoodLog, Count = n });
        return Known(EquatableArray.From(result));
    }
}

/// <summary>
/// One still-open action of an admitted method (a shelter shell is admitted without a stock check) and the
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
    public Fact<IReadOnlyDictionary<Resource, long>> Owed { get; init; }

    /// <summary>The open costs of admitted-but-unplaced methods.</summary>
    public EquatableArray<AdmittedCost> Admitted { get; init; }

    /// <summary>The wood latch's WoodLog floor, 0 while the latch is off.</summary>
    public long WoodFloor { get; init; }
}

public static partial class PolicyFunctions
{
    internal static readonly Resource ConstructionWoodLog = new("WoodLog");

    /// <summary>
    /// The stock MaintainResource must reach for construction, per resource: a blueprint's owed material, the
    /// open costs of admitted methods (each action once) and the wood latch's floor, each counted where the
    /// stock falls short of it (the latch whenever it is on), merged by maximum. It is an absolute stock level;
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
        bool Short(Resource resource, long need) => input.Stock.Count(resource).TryGetValue(out var have) && need > have;
        if (input.Owed.TryGetValue(out var owed))
        {
            foreach (var (resource, n) in owed)
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
        Raise(ConstructionWoodLog, input.WoodFloor);
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
            Owed = f.ConstructionDeficit,
            Admitted = f.Admitted,
            WoodFloor = WoodLatchFloor(l.Wood, p),
        });

    /// <summary>The WoodLog floor the wood latch asks for: WoodTarget while latched, 0 otherwise.</summary>
    internal static long WoodLatchFloor(bool latched, RoundsPolicy p) => latched ? p.WoodTarget : 0;
}
