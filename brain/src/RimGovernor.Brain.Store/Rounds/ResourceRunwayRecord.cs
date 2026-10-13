using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>
/// One resource's persisted runway forecast. Unknown observations persist as null, not as a known zero or false
/// (Go <c>ResourceRunwayRecord</c>).
/// </summary>
public sealed record ResourceRunwayRecord
{
    public Resource Resource { get; init; }

    public Tick Tick { get; init; }

    public double WindowDays { get; init; }

    public long Reserve { get; init; }

    public long? Stock { get; init; }

    public long? SurfaceOre { get; init; }

    public double? ConsumptionPerDay { get; init; }

    public double? StockDays { get; init; }

    public double? DaysLeft { get; init; }

    public bool? Deficit { get; init; }

    public long Target { get; init; }
}

public sealed partial class Store
{
    /// <summary>The persisted form of the review's runway forecasts (Go <c>resourceRunwayRecords</c>).</summary>
    internal static EquatableArray<ResourceRunwayRecord> ResourceRunwayRecords(IReadOnlyList<ResourceRunway> rows) =>
        EquatableArray.From(
            rows.Select(r => new ResourceRunwayRecord
            {
                Resource = r.Resource,
                Tick = r.Tick,
                WindowDays = r.WindowDays,
                Reserve = r.Reserve,
                Stock = PersistedFact.Value(r.Stock),
                SurfaceOre = PersistedFact.Value(r.SurfaceOre),
                ConsumptionPerDay = PersistedFact.Value(r.ConsumptionPerDay),
                StockDays = PersistedFact.Value(r.StockDays),
                DaysLeft = PersistedFact.Value(r.DaysLeft),
                Deficit = PersistedFact.Value(r.Deficit),
                Target = r.Target,
            })
        );
}
