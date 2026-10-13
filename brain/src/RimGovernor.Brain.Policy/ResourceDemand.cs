namespace RimGovernor.Brain.Policy;

/// <summary>Identifies an output definition and, when required, its stuff. Empty Stuff in demand means any stuff; in stock it means an unstuffed item.</summary>
public sealed record ResourceKey
{
    public Resource Def { get; init; }

    public Resource Stuff { get; init; }
}

public sealed record ResourceQuantity
{
    public ResourceKey Key { get; init; } = new();

    public long Count { get; init; }
}

/// <summary>
/// Count is an unmet quantity in output, a stock target or a remaining bill-of-materials cost in input.
/// Larger Priority is more important.
/// </summary>
public sealed record ResourceDemand
{
    public ResourceKey Key { get; init; } = new();

    public long Count { get; init; }

    public int Priority { get; init; }
}

/// <summary>
/// Combines acquisition targets and economic stock floors. EconomicFloors is EconomicReserves's result;
/// PlannedConstruction must exclude commitments already included in those floors and materials already
/// delivered. Stock is a complete usable census: an absent key means zero only when known. Pending
/// acquisition is not stock and cannot certify a satisfied shortage.
/// </summary>
public sealed record ResourceDemandInput
{
    public EquatableArray<ResourceDemand> Targets { get; init; }

    public IReadOnlyDictionary<string, long> EconomicFloors { get; init; } = new Dictionary<string, long>();

    public EquatableArray<ResourceDemand> PlannedConstruction { get; init; }

    public Fact<EquatableArray<ResourceQuantity>> Stock { get; init; }
}

public static partial class PolicyFunctions
{
    internal const long MaxAcquisitionCount = 1_000_000_000;

    internal static bool ValidAcquisitionKey(ResourceKey k) =>
        ValidResource(k.Def) && (k.Stuff.Value.Length == 0 || ValidResource(k.Stuff));

    internal static bool ValidAcquisitionCount(long n) => n >= 0 && n <= MaxAcquisitionCount;

    internal static bool AcquisitionKeyLess(ResourceKey a, ResourceKey b)
    {
        if (a.Def != b.Def)
        {
            return a.Def < b.Def;
        }
        // Exact stuff consumes stock/yield before the any-stuff remainder.
        if ((a.Stuff.Value.Length == 0) != (b.Stuff.Value.Length == 0))
        {
            return a.Stuff.Value.Length != 0;
        }
        return a.Stuff < b.Stuff;
    }

    internal static bool ValidDemand(ResourceDemand d) =>
        ValidAcquisitionKey(d.Key) && ValidAcquisitionCount(d.Count) && d.Priority >= 1 && d.Priority <= 100;

    /// <summary>
    /// Takes the maximum overlapping stock target/floor, adds planned costs, then spends each stock unit once.
    /// Exact-stuff obligations take precedence over any-stuff obligations. Floors have priority 1; merged rows
    /// retain their highest priority. Output is canonical and contains no zero rows. Throws
    /// <see cref="DomainException"/> on invalid input.
    /// </summary>
    public static Fact<EquatableArray<ResourceDemand>> BuildResourceDemand(ResourceDemandInput input)
    {
        var unknown = Unknown<EquatableArray<ResourceDemand>>();
        var known = input.Stock.TryGetValue(out var stock);
        var wanted = new Dictionary<ResourceKey, ResourceDemand>();
        foreach (var target in input.Targets)
        {
            if (!ValidDemand(target))
            {
                throw new DomainException("invalid resource target");
            }
            var prior = wanted.GetValueOrDefault(target.Key) ?? new ResourceDemand();
            wanted[target.Key] = target with { Count = Math.Max(target.Count, prior.Count), Priority = Math.Max(target.Priority, prior.Priority) };
        }
        foreach (var (def, count) in input.EconomicFloors)
        {
            var key = new ResourceKey { Def = new Resource(def) };
            if (!ValidAcquisitionKey(key) || !ValidAcquisitionCount(count))
            {
                throw new DomainException("invalid economic floor");
            }
            var prior = wanted.GetValueOrDefault(key) ?? new ResourceDemand();
            wanted[key] = new ResourceDemand { Key = key, Count = Math.Max(count, prior.Count), Priority = Math.Max(1, prior.Priority) };
        }
        // A definition-wide floor includes its stuff-specific stock targets, rather than asking for both the
        // floor and the same units a second time.
        var exact = new Dictionary<Resource, long>();
        foreach (var (key, d) in wanted)
        {
            if (key.Stuff.Value.Length != 0)
            {
                exact[key.Def] = exact.GetValueOrDefault(key.Def) + d.Count;
            }
        }
        foreach (var (key, d) in wanted.ToList())
        {
            if (key.Stuff.Value.Length == 0)
            {
                wanted[key] = d with { Count = Math.Max(0, d.Count - exact.GetValueOrDefault(key.Def)) };
            }
        }
        foreach (var planned in input.PlannedConstruction)
        {
            if (!ValidDemand(planned))
            {
                throw new DomainException("invalid planned construction cost");
            }
            var prior = wanted.GetValueOrDefault(planned.Key) ?? new ResourceDemand();
            var merged = planned with { Count = planned.Count + prior.Count, Priority = Math.Max(planned.Priority, prior.Priority) };
            if (!ValidAcquisitionCount(merged.Count))
            {
                throw new DomainException("combined resource demand exceeds bound");
            }
            wanted[planned.Key] = merged;
        }
        var available = new Dictionary<ResourceKey, long>();
        foreach (var s in known ? stock : default)
        {
            if (!ValidAcquisitionKey(s.Key) || !ValidAcquisitionCount(s.Count))
            {
                throw new DomainException("invalid acquisition stock");
            }
            if (!available.TryAdd(s.Key, s.Count))
            {
                throw new DomainException("duplicate acquisition stock");
            }
        }
        if (!known)
        {
            return unknown;
        }
        var keys = wanted.Keys.OrderBy(k => k, Comparer<ResourceKey>.Create((a, b) => AcquisitionKeyLess(a, b) ? -1 : AcquisitionKeyLess(b, a) ? 1 : 0)).ToList();
        var remainingByDef = new Dictionary<Resource, long>();
        foreach (var (key, count) in available)
        {
            remainingByDef[key.Def] = remainingByDef.GetValueOrDefault(key.Def) + count;
        }
        var result = new List<ResourceDemand>();
        foreach (var key in keys)
        {
            var d = wanted[key];
            var have = available.GetValueOrDefault(key);
            if (key.Stuff.Value.Length == 0)
            {
                have = remainingByDef.GetValueOrDefault(key.Def);
            }
            var used = Math.Min(d.Count, have);
            d = d with { Count = d.Count - used };
            remainingByDef[key.Def] = remainingByDef.GetValueOrDefault(key.Def) - used;
            if (d.Count > 0)
            {
                result.Add(d);
            }
        }
        return Known(EquatableArray.From(result));
    }
}
