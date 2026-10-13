using System.Globalization;

namespace RimGovernor.Brain.Policy;

// A store's zone follows its room's region. A room that grows is filled by StoreGeometryEdits (cells added to
// the standing zone), a room that is replaced moves the zone to the successor's ground (StoreSiteMoves: the
// replacement is created with the declared filter and priority before the old zone is deleted, matched by
// position), and a room that shrinks, or whose floor furniture now stands on, loses exactly those cells here.
// Stock on a given-up cell drains by vanilla hauling: no haul action, no fill memory.
public static partial class PolicyFunctions
{
    /// <summary>
    /// Reports a cell holding furniture a zone must stay off: a building, blueprint or frame that is not a
    /// shelf (a shelf is the zone's own storage).
    /// </summary>
    internal static bool Furnished(SiteCell c)
    {
        foreach (var t in c.Things)
        {
            if ((t.Flags & (FlagEdifice | FlagBlueprint | FlagFrame)) != 0 && t.Def != ShelfDefinition)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Cuts each zone of a whole-footprint store back to the ground its serving sites still cover: cells
    /// outside every serving site's footprint (the room shrank or reshaped), in a site's Avoid list, or under
    /// furniture are removed. A zone left with no cell is not shrunk: it is moved or deleted by StoreSiteMoves.
    /// Zones with a settings edit pending wait.
    /// </summary>
    internal static List<StockpileEdit> StoreShrinkEdits(
        IReadOnlyList<StockpileZone> zones,
        IReadOnlyList<StoreSite> sites,
        StockpileOpen open,
        IReadOnlyList<StockpileEdit> settings
    )
    {
        var pending = new HashSet<string>(settings.Select(e => e.Zone), StringComparer.Ordinal);
        var output = new List<StockpileEdit>();
        foreach (var z in zones)
        {
            if (pending.Contains(z.Id) || z.Role.Length == 0)
            {
                continue;
            }
            var keep = new HashSet<Cell>();
            var whole = false;
            foreach (var site in sites)
            {
                if (!site.WholeFootprint() || site.Regions is not null || !site.Serves(z))
                {
                    continue;
                }
                whole = true;
                foreach (var c in WithoutCells(site.Footprint(), site.Avoid))
                {
                    keep.Add(c);
                }
            }
            if (!whole)
            {
                continue;
            }
            var drop = z.Cells.Where(c => !keep.Contains(c) || Furnished(open.CellAt(c))).ToList();
            if (drop.Count == 0 || drop.Count == z.Cells.Count)
            {
                continue;
            }
            output.Add(new StockpileEdit
            {
                Kind = StockpileEditKind.Shrink,
                Zone = z.Id,
                Role = z.Role,
                RemovedCells = StockpileSorted(drop),
                Explanation = string.Create(
                    CultureInfo.InvariantCulture,
                    $"stockpile {z.Id} ({z.Role}): room gave up {drop.Count} cells, remove them; stock there drains by vanilla hauling"
                ),
            });
        }
        return output;
    }

    /// <summary>Splits cells into cardinally connected parts, each sorted, ordered by their lowest cell.</summary>
    internal static List<List<Cell>> ConnectedComponents(IReadOnlyList<Cell> cells)
    {
        var member = CellSet(cells);
        var seen = new HashSet<Cell>();
        var output = new List<List<Cell>>();
        foreach (var start in StockpileSorted(cells))
        {
            if (!seen.Add(start))
            {
                continue;
            }
            var part = new List<Cell> { start };
            for (var i = 0; i < part.Count; i++)
            {
                foreach (var n in StockpileNeighbours(part[i]))
                {
                    if (member.Contains(n) && seen.Add(n))
                    {
                        part.Add(n);
                    }
                }
            }
            output.Add(StockpileSorted(part));
        }
        return output;
    }
}
