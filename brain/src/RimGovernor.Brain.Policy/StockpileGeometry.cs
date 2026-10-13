using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one record of ground a pass has already promised to a zone: every store sited in a pass claims its cells
/// here, so two stores never overlap. A cell is claimed once and never released within the pass.
/// </summary>
internal sealed class ReservedGround : HashSet<Cell>
{
    /// <summary>Reserves cells.</summary>
    public void Claim(IEnumerable<Cell> cells)
    {
        foreach (var c in cells)
        {
            Add(c);
        }
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Reports a warehouse zone's role: every warehouse site is a general store, the first planned room's
    /// "general" or a further one's "general:&lt;room id&gt;".
    /// </summary>
    internal static bool IsWarehouseRole(string role) => StockpileRolePrefix(role) == DomainConstants.GeneralRole;

    internal static string StockpileRolePrefix(string role)
    {
        var i = role.IndexOf(':', StringComparison.Ordinal);
        return i < 0 ? role : role[..i];
    }

    internal static HashSet<Cell> CellSet(IEnumerable<Cell> cells) => [.. cells];

    internal static bool StockpileTouches(IEnumerable<Cell> cells, HashSet<Cell> room) => cells.Any(room.Contains);

    /// <summary>
    /// Fills cleared ground reachable from a whole-footprint store's first owned zone. Once that growth stands,
    /// compatible adjacent fragments are deleted; the following review adds their freed cells to the survivor.
    /// Blockers and unrelated zones keep fragments separate until the ground connects them. Native zoneability
    /// and connected-shape checks still govern every write.
    /// </summary>
    internal static List<StockpileEdit> StoreGeometryEdits(
        IReadOnlyList<StockpileZone> zones,
        IReadOnlyList<StoreSite> sites,
        StockpileOpen open,
        IReadOnlyList<StockpileEdit> settings
    )
    {
        var pending = new HashSet<string>(settings.Select(e => e.Zone), StringComparer.Ordinal);
        var edits = new List<StockpileEdit>();
        foreach (var site in sites)
        {
            if (site.Width > 0 || site.Height > 0)
            {
                continue;
            }
            var fragments = new List<StockpileZone>();
            var settingsPending = false;
            foreach (var zone in zones)
            {
                if (site.Serves(zone))
                {
                    fragments.Add(zone);
                    settingsPending = settingsPending || pending.Contains(zone.Id);
                }
            }
            if (fragments.Count == 0 || settingsPending)
            {
                continue;
            }
            fragments = [.. fragments.OrderBy(f => f.Id, StringComparer.Ordinal)];
            var keep = fragments[0];
            var footprint = CellSet(WithoutCells(site.Footprint(), site.Avoid));
            var owned = CellSet(keep.Cells);

            bool Allowed(Cell cell)
            {
                if (!footprint.Contains(cell) || open.Protected.Contains(cell) || open.Taken.Contains(cell))
                {
                    return false;
                }
                if (!open.Cells.TryGetValue(cell, out var observed))
                {
                    return false;
                }
                if (site.Roofed)
                {
                    if (!observed.Roofed.TryGetValue(out var roofed) || !roofed)
                    {
                        return false;
                    }
                }
                return true;
            }

            var reached = new HashSet<Cell>();
            var queue = new List<Cell>();
            foreach (var cell in keep.Cells)
            {
                if (Allowed(cell))
                {
                    reached.Add(cell);
                    queue.Add(cell);
                }
            }
            var add = new List<Cell>();
            for (var i = 0; i < queue.Count; i++)
            {
                foreach (var cell in StockpileNeighbours(queue[i]))
                {
                    if (reached.Contains(cell) || !Allowed(cell) || (!owned.Contains(cell) && !open.Ok(cell)))
                    {
                        continue;
                    }
                    reached.Add(cell);
                    queue.Add(cell);
                    if (!owned.Contains(cell))
                    {
                        add.Add(cell);
                    }
                }
            }
            if (add.Count > 0)
            {
                foreach (var cell in add)
                {
                    open.Taken.Add(cell);
                }
                edits.Add(new StockpileEdit
                {
                    Kind = StockpileEditKind.Grow,
                    Zone = keep.Id,
                    Role = keep.Role,
                    AddedCells = StockpileSorted(add),
                    Explanation = string.Create(CultureInfo.InvariantCulture, $"stockpile {keep.Id} ({keep.Role}): fill {add.Count} cleared site cells"),
                });
                continue;
            }
            foreach (var fragment in fragments.Skip(1))
            {
                if (fragment.Filter != keep.Filter || fragment.Priority != keep.Priority)
                {
                    continue;
                }
                var connected = false;
                var clear = true;
                foreach (var cell in fragment.Cells)
                {
                    var observed = open.CellAt(cell);
                    var known = observed.Walkable.TryGetValue(out var walkable);
                    clear = clear && Allowed(cell) && known && walkable && !observed.Occupied();
                    foreach (var neighbour in StockpileNeighbours(cell))
                    {
                        connected = connected || reached.Contains(neighbour);
                    }
                }
                if (clear && connected)
                {
                    edits.Add(new StockpileEdit
                    {
                        Kind = StockpileEditKind.Delete,
                        Zone = fragment.Id,
                        Role = fragment.Role,
                        Explanation = $"stockpile {fragment.Id} ({fragment.Role}): consolidate into {keep.Id}",
                    });
                }
            }
        }
        return edits;
    }
}
