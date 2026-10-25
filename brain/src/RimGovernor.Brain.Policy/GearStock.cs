using GearStock = RimGovernor.Contracts.Wire.Observations.GearStock;
namespace RimGovernor.Brain.Policy;

/// <summary>Claims against an actual native stock row; allocation never edits its observed count.</summary>
public sealed record GearStockBalance(GearStock Observed)
{
    public int Allocated { get; init; }
    public int Count => Observed.Count - Allocated;
}

public static class GearStockRules
{
    public static int QualityIndex(this GearStock row) => row.Quality is RimGovernor.Contracts.Wire.Observations.ItemQuality.Graded g ? (int)g.Category : 2;
    public static bool Serviceable(this GearStock row) => row.HpBand >= StockpileRolesConstants.GearHitPointFloor * 10 && row.QualityIndex() >= 2;
    /// <summary>
    /// Stored options assigned by the ensemble planner already satisfied one gap. Remove those physical units
    /// before netting its remaining bill demand.
    /// </summary>
    internal static List<GearStockBalance> UnassignedGearStock(IReadOnlyList<GearStock> stock, IReadOnlyList<GearLoadout> loadouts)
    {
        var @out = stock.Select(row => new GearStockBalance(row)).ToList();
        foreach (var l in loadouts)
        {
            foreach (var g in l.Gaps)
            {
                if (g.Source != GearSource.Stored || !GearLoadoutRules.GearGapWanted(l.Role, g))
                {
                    continue;
                }
                var band = Math.Min(9, (int)Math.Floor(g.Wanted.Condition * 10));
                for (var i = 0; i < @out.Count; i++)
                {
                    var balance = @out[i];
                    var row = balance.Observed;
                    if (balance.Count > 0 && row.DefName == g.Wanted.Definition.Value && (row.Stuff ?? "") == g.Wanted.Stuff.Value && row.QualityIndex() == g.Wanted.Quality && row.HpBand == band)
                    {
                        @out[i] = balance with { Allocated = balance.Allocated + 1 };
                        break;
                    }
                }
            }
        }
        return @out;
    }

    internal static EquatableArray<EquatableArray<Amount>> GearBatchIngredients(EquatableArray<EquatableArray<Amount>> slots, int count)
    {
        var @out = new List<EquatableArray<Amount>>(slots.Count);
        foreach (var slot in slots)
        {
            @out.Add(EquatableArray.From(slot.Select(a => new Amount(a.Resource, a.Count * count))));
        }
        return EquatableArray.From(@out);
    }
}
