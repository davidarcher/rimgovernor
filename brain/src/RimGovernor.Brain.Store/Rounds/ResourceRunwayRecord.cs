using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>
/// One resource's persisted runway forecast. An Unrated rating persists as a null Deficit, an Idle one as a Deficit with no DaysLeft, and a Burning one with every rate field.
/// </summary>
public sealed record ResourceRunwayRecord
{
    public Resource Resource { get; init; }

    public Tick Tick { get; init; }

    public double WindowDays { get; init; }

    public long Reserve { get; init; }

    public long Stock { get; init; }

    public long SurfaceOre { get; init; }

    public double? ConsumptionPerDay { get; init; }

    public double? StockDays { get; init; }

    public double? DaysLeft { get; init; }

    /// <summary>Null while the rating is Unrated; otherwise the rating's deficit. A null DaysLeft with a Deficit is Idle.</summary>
    public bool? Deficit { get; init; }

    public long Target { get; init; }
}

public sealed partial class Store
{
    /// <summary>The persisted form of the review's runway forecasts.</summary>
    internal static EquatableArray<ResourceRunwayRecord> ResourceRunwayRecords(IReadOnlyList<ResourceRunway> rows) =>
        EquatableArray.From(rows.Select(RunwayRecord));

    private static ResourceRunwayRecord RunwayRecord(ResourceRunway r)
    {
        var record = new ResourceRunwayRecord
        {
            Resource = r.Resource,
            Tick = r.Tick,
            WindowDays = r.WindowDays,
            Reserve = r.Reserve,
            Stock = r.Stock,
            SurfaceOre = r.SurfaceOre,
            Target = r.Target,
        };
        if (r.Rating is RunwayRating.Idle idle)
        {
            return record with { Deficit = idle.Deficit };
        }
        if (r.Rating is RunwayRating.Burning burning)
        {
            return record with
            {
                ConsumptionPerDay = burning.PerDay,
                StockDays = burning.StockDays,
                DaysLeft = burning.DaysLeft,
                Deficit = burning.Deficit,
            };
        }
        return r.Rating is RunwayRating.Unrated ? record : throw new InvalidOperationException($"runway rating {r.Rating.GetType().Name} has no persisted form");
    }

    /// <summary>The rating a persisted runway row names; a row mixing the shapes is corrupt.</summary>
    internal static RunwayRating RunwayRatingOf(ResourceRunwayRecord row) =>
        (row.ConsumptionPerDay, row.StockDays, row.DaysLeft, row.Deficit) switch
        {
            (null, null, null, null) => new RunwayRating.Unrated(),
            (null, null, null, { } deficit) => new RunwayRating.Idle(deficit),
            ({ } rate, { } stockDays, { } daysLeft, { } deficit) => new RunwayRating.Burning(rate, stockDays, daysLeft, deficit),
            _ => throw new InvalidDataException($"runway row {row.Resource.Value} mixes its rating fields"),
        };
}
