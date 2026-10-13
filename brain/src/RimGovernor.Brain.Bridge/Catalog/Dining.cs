using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_dining.go: the furniture the dining and recreation planners place, chosen by rules over the catalog rows
// and never by name. The chair is the buildable ThingDef the game lets a pawn sit on (BuildingProperties.isSittable)
// with the most Comfort per unit of cost, taken at its best stuff; ties go to the name. The table is the buildable
// ThingDef whose surfaceType is Eat with the smallest footprint, then the cheapest, then the name. The pin is the
// recreation foothold, whose lane is the far end of its watch stand distance range plus the pin's own cell, one cell
// when the building is not watched from a distance.

public sealed partial class DefinitionCatalog
{
    /// <summary>The dining furniture; a catalog with no chair, no table or no foothold is an error, and so is a shape the dining template cannot lay out.</summary>
    public P.DiningFurniture DiningFurniture()
    {
        var chair = DiningChair();
        var table = DiningTable();
        var foothold = RecreationFoothold();
        var lane = ThingRow(foothold).building?.watchBuildingStandDistanceRange is { } stand ? stand.max + 1 : 1;
        var furniture = new P.DiningFurniture { Chair = PieceShape(chair), Table = PieceShape(table), Pin = PieceShape(foothold), Lane = lane };
        try
        {
            furniture.Validate();
        }
        catch (Exception e) when (e is Domain.DomainException)
        {
            throw new CatalogException(e.Message);
        }

        return furniture;
    }

    // A def's plannable footprint: its size at rotation North.
    private P.InteriorPieceDef PieceShape(string def)
    {
        var size = ThingRow(def).size;
        return new P.InteriorPieceDef(def, new Domain.Cell(size.x, size.z), P.RoomRole.Unset, null);
    }

    /// <summary>The sittable building with the most comfort per cost.</summary>
    public string DiningChair()
    {
        string? best = null;
        var bestRatio = double.NegativeInfinity;
        foreach (var (name, row) in _thingDefs)
        {
            if (!Buildable(row) || row.building is not { isSittable: true })
            {
                continue;
            }

            var stuffs = AllowedStuffs(name);
            var ratio = double.NegativeInfinity;
            foreach (var stuff in stuffs.Count == 0 ? [string.Empty] : stuffs)
            {
                var (comfort, shown) = ShownStatValue(name, stuff, StatNames.Comfort);
                if (!shown)
                {
                    continue;
                }

                var cost = CostValueOf(name, stuff);
                if (cost <= 0)
                {
                    throw new CatalogException($"sittable building {name} costs nothing with stuff \"{stuff}\"");
                }

                ratio = Math.Max(ratio, comfort / cost);
            }

            if (ratio > bestRatio || (ratio == bestRatio && string.CompareOrdinal(name, best) < 0))
            {
                (best, bestRatio) = (name, ratio);
            }
        }

        return best is null || double.IsNegativeInfinity(bestRatio) ? throw new CatalogException("catalog has no buildable sittable def with a comfort stat") : best;
    }

    /// <summary>The buildable eating surface with the smallest footprint, then the lowest cost.</summary>
    public string DiningTable()
    {
        var found = _thingDefs
            .Where(pair => Buildable(pair.Value) && pair.Value.surfaceType == SurfaceType.Eat)
            .Select(pair => (Name: pair.Key, Area: pair.Value.size.x * pair.Value.size.z, Cost: CheapestCostValue(pair.Key)))
            .OrderBy(c => c.Area).ThenBy(c => c.Cost).ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        return found.Count == 0 ? throw new CatalogException("catalog has no buildable def with an eating surface") : found[0].Name;
    }
}
