using O = RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>Aggregate the canonical native resource rows for a policy query.</summary>
public static class ResourceStockRules
{
    public static EquatableArray<Amount> Amounts(IEnumerable<O.Quantity> rows) =>
        EquatableArray.From(rows.Select(row => new Amount(new Resource(row.DefName), row.Units)));

    public static EquatableArray<Amount> ObservedStock(this RoundsFacts facts) => Amounts(facts.Census.Resources);
    public static EquatableArray<Amount> ObservedStock(this ResourceFacts facts) => Amounts(facts.Census.Resources);
    public static EquatableArray<Amount> ObservedStock(this ColonyForecastFacts facts) => Amounts(facts.Census.Resources);
    public static EquatableArray<Amount> ObservedStock(this MedicalReserveObservation facts) => Amounts(facts.Census.Resources);
    public static long WoodStock(this RoundsFacts facts) => facts.Census.Resources
        .Where(row => row.DefName == ConstructionDemandRules.ConstructionWoodLog.Value).Sum(row => row.Units);

    public static IReadOnlyDictionary<Resource, long> Count(IEnumerable<O.Quantity> rows)
    {
        var stock = new Dictionary<Resource, long>();
        foreach (var row in rows)
        {
            var resource = new Resource(row.DefName);
            stock[resource] = stock.GetValueOrDefault(resource) + row.Units;
        }
        return stock;
    }
}