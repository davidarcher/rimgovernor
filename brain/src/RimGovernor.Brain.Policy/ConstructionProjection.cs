namespace RimGovernor.Brain.Policy;

/// <summary>Standing deficit and admitted costs overlap: take their maximum per exact resource.</summary>
public sealed record ConstructionInputs
{
    public Fact<IReadOnlyDictionary<Resource, long>> Deficit { get; init; }
    public EquatableArray<AdmittedCost> Admitted { get; init; }
}

/// <summary>Material quantities, never a construction completion time.</summary>
public sealed record ConstructionMaterial(Resource Resource, long Need, long Stock, long ShortfallUnits);

public sealed record ConstructionProjection(EquatableArray<ConstructionMaterial> Materials);

public static partial class PolicyFunctions
{
    internal static Fact<ConstructionProjection> ProjectConstruction(ConstructionInputs input, Fact<EquatableArray<Amount>> stockFacts)
    {
        if (!input.Deficit.TryGetValue(out var owed) || !stockFacts.TryGetValue(out var rows))
        {
            return Unknown<ConstructionProjection>();
        }
        var needs = new Dictionary<Resource, long>(owed);
        foreach (var (resource, count) in AdmittedNeeds(input.Admitted))
        {
            needs[resource] = Math.Max(needs.GetValueOrDefault(resource), count);
        }
        var stocks = new Dictionary<Resource, long>();
        foreach (var row in rows)
        {
            if (!ValidResource(row.Resource) || row.Count < 0)
            {
                return Unknown<ConstructionProjection>();
            }
            stocks[row.Resource] = checked(stocks.GetValueOrDefault(row.Resource) + row.Count);
        }
        var materials = new List<ConstructionMaterial>();
        foreach (var (resource, need) in needs.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
        {
            if (!ValidResource(resource) || need < 0)
            {
                return Unknown<ConstructionProjection>();
            }
            var stock = stocks.GetValueOrDefault(resource);
            materials.Add(new ConstructionMaterial(resource, need, stock, Math.Max(0, need - stock)));
        }
        return Known(new ConstructionProjection(EquatableArray.From(materials)));
    }
}
