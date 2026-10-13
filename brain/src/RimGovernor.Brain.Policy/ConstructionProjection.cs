namespace RimGovernor.Brain.Policy;

/// <summary>A build material pool the projector tracks.</summary>
public enum ConstructionClass
{
    [WireName("wood")]
    Wood,
    [WireName("stone")]
    Stone,
    [WireName("steel")]
    Steel,
}

/// <summary>
/// What standing and admitted construction still needs and the stock it is measured against. Deficit and Stock
/// are the facts MaintainResource's floors already read; Admitted carries the previewed costs of admitted
/// shelter methods.
/// </summary>
public sealed record ConstructionInputs
{
    public Fact<IReadOnlyDictionary<Resource, long>> Deficit { get; init; }

    public EquatableArray<AdmittedCost> Admitted { get; init; }

    public Fact<EquatableArray<Amount>> Stock { get; init; }

    public ItemFacts Items { get; init; } = new();

    internal ConstructionClass? ClassOf(Resource r)
    {
        if (r == PolicyFunctions.ConstructionWoodLog)
        {
            return ConstructionClass.Wood;
        }
        if (r.Value == "Steel")
        {
            return ConstructionClass.Steel;
        }
        if (Items.IsStoneBlocks(r))
        {
            return ConstructionClass.Stone;
        }
        return null;
    }
}

/// <summary>
/// One material pool: Need is the larger of the standing deficit and the admitted methods' open costs (the two
/// overlap, as in ConstructionResourceNeeds), Stock the census, ShortfallDays the share of the horizon the
/// missing units leave unsupplied.
/// </summary>
public sealed record ConstructionClassProjection
{
    public ConstructionClass Class { get; init; }

    public long Need { get; init; }

    public long Stock { get; init; }

    public long ShortfallUnits { get; init; }

    public double ShortfallDays { get; init; }
}

public sealed record ConstructionProjection
{
    public EquatableArray<ConstructionClassProjection> Classes { get; init; }

    /// <summary>The largest per-class shortfall.</summary>
    public double ShortfallDays { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Compares construction material need with stock. Construction is a one-shot demand with no native burn
    /// rate, so units missing now are projected as the matching share of the horizon unsupplied (all of it
    /// when nothing is in stock). An unknown deficit or stock census is an unknown projection, never zero.
    /// </summary>
    internal static Fact<ConstructionProjection> ProjectConstruction(ConstructionInputs input)
    {
        if (!input.Deficit.TryGetValue(out var owed) || !input.Stock.TryGetValue(out var rows))
        {
            return Unknown<ConstructionProjection>();
        }
        var need = new Dictionary<Resource, long>(owed);
        foreach (var (r, n) in AdmittedNeeds(input.Admitted))
        {
            need[r] = Math.Max(need.GetValueOrDefault(r), n);
        }
        var needs = new Dictionary<ConstructionClass, long>();
        var stock = new Dictionary<ConstructionClass, long>();
        foreach (var (r, n) in need)
        {
            if (input.ClassOf(r) is { } c && n > 0)
            {
                needs[c] = needs.GetValueOrDefault(c) + n;
            }
        }
        foreach (var row in rows)
        {
            if (input.ClassOf(row.Resource) is { } c)
            {
                stock[c] = stock.GetValueOrDefault(c) + row.Count;
            }
        }
        var classes = new List<ConstructionClassProjection>();
        var worst = 0.0;
        foreach (var (c, n) in needs)
        {
            var have = stock.GetValueOrDefault(c);
            var shortUnits = Math.Max(0, n - have);
            var row = new ConstructionClassProjection
            {
                Class = c,
                Need = n,
                Stock = have,
                ShortfallUnits = shortUnits,
                ShortfallDays = ProjectionHorizonDays * shortUnits / n,
            };
            worst = Math.Max(worst, row.ShortfallDays);
            classes.Add(row);
        }
        return Known(new ConstructionProjection
        {
            Classes = EquatableArray.From(classes.OrderBy(c => Wire.Name(c.Class), StringComparer.Ordinal)),
            ShortfallDays = worst,
        });
    }
}
