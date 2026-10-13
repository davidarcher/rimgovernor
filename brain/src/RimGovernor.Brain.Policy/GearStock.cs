namespace RimGovernor.Brain.Policy;

/// <summary>A bounded aggregate of usable apparel in native storage.</summary>
public sealed record GearStock
{
    public Resource Definition { get; init; }
    public Resource Stuff { get; init; }
    public int Quality { get; init; }
    public int HPBand { get; init; }
    public int Count { get; init; }

    /// <summary>
    /// Reports stock over the gear floors (<see cref="DomainConstants.GearHitPointFloor"/>, Normal quality): what
    /// the gear stores keep; the rest is sale gear or burnable waste.
    /// </summary>
    public bool Serviceable() => HPBand >= DomainConstants.GearHitPointFloor * 10 && Quality >= 2;
}

/// <summary>The definition and stuff a stock row or need is netted under. (Go <c>gearStockKey</c>.)</summary>
internal readonly record struct GearStockKey(Resource Definition, Resource Stuff);

public static partial class PolicyFunctions
{
    /// <summary>
    /// Stored options assigned by the ensemble planner already satisfied one gap. Remove those physical units
    /// before netting its remaining bill demand.
    /// </summary>
    internal static List<GearStock> UnassignedGearStock(IReadOnlyList<GearStock> stock, IReadOnlyList<GearLoadout> loadouts)
    {
        var @out = new List<GearStock>(stock);
        foreach (var l in loadouts)
        {
            foreach (var g in l.Gaps)
            {
                if (g.Source != GearSource.Stored || g.Gain <= GearGapThreshold(l.Role))
                {
                    continue;
                }
                var band = Math.Min(9, (int)Math.Floor(g.Wanted.Condition * 10));
                for (var i = 0; i < @out.Count; i++)
                {
                    var row = @out[i];
                    if (row.Count > 0 && row.Definition == g.Wanted.Definition && row.Stuff == g.Wanted.Stuff && row.Quality == g.Wanted.Quality && row.HPBand == band)
                    {
                        @out[i] = row with { Count = row.Count - 1 };
                        break;
                    }
                }
            }
        }
        return @out;
    }

    internal static IReadOnlyList<IReadOnlyList<Amount>> GearBatchIngredients(IReadOnlyList<IReadOnlyList<Amount>> slots, int count)
    {
        var @out = new List<IReadOnlyList<Amount>>(slots.Count);
        foreach (var slot in slots)
        {
            @out.Add(slot.Select(a => new Amount(a.Resource, a.Count * count)).ToList());
        }
        return @out;
    }
}
