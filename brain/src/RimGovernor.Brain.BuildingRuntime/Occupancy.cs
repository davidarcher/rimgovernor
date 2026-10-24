using System.Globalization;
using System.Text.RegularExpressions;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The occupied cells of a layout and the interior origins of the planned rooms in flight.</summary>
internal sealed record LayoutOccupancy(HashSet<Cell> Cells, HashSet<Cell> Origins, HashSet<int> Rooms);

partial class Rounder
{
    // Matches the method ids of the plans keyed by a planned room's interior origin: its dig, shell, bedroom
    // steps, and the tomb and jail pieces.
    [GeneratedRegex(@"^(?:plan-dig-[^-]+-[^-]+|bedroom-[^-]+|[^-]+-shell|(?:tomb|jail)-place)-([0-9]+)-([0-9]+)(?:-.*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoomMethodPattern();

    // Matches the method ids of the plans keyed by a planned room's id: every room reconcile (RoomReconcileName,
    // "<role>-shell-room<id>"), whose role may itself hold a dash (isolation-room).
    [GeneratedRegex(@"^.+?-shell-room([0-9]+)(?:-.*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoomIdPattern();

    /// <summary>The ids of the planned rooms an open (not retired) plan's shell method works on; a re-sited room keeps its id, so its work is still found.</summary>
    public static HashSet<int> RoomMethodIds(IEnumerable<PlanState> plans)
    {
        var output = new HashSet<int>();
        foreach (var p in plans)
        {
            if (!p.Retired
                && RoomIdPattern().Match(p.Method.Value) is { Success: true } m
                && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                output.Add(id);
            }
        }
        return output;
    }

    /// <summary>
    /// The interior origins of the planned rooms an open (not retired) plan's method works on.
    /// </summary>
    public static HashSet<Cell> RoomMethodOrigins(IEnumerable<PlanState> plans)
    {
        var output = new HashSet<Cell>();
        foreach (var p in plans)
        {
            if (p.Retired)
            {
                continue;
            }
            var m = RoomMethodPattern().Match(p.Method.Value);
            if (m.Success
                && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(m.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var z))
            {
                output.Add(new Cell(x, z));
            }
        }
        return output;
    }

    /// <summary>
    /// The cells of ours a room must not lose: every census building, the full footprint of every blueprint and
    /// frame site and of every journal claim whose work has not closed gone (the definition's size when the
    /// projection read it, else the anchor), and every player edifice and doorway (a door blueprint or frame
    /// included) the planning cells report. RoomGrowth.Fixed reads it. Null without a complete census.
    /// </summary>
    public static HashSet<Cell>? OccupiedCells(ColonyProjection projection)
    {
        if (!projection.Facts.CurrentConstruction.TryGetValue(out var census) || !census.Colony)
        {
            return null;
        }
        var sizes = new Dictionary<string, Bounds>(StringComparer.Ordinal);
        foreach (var d in projection.Definitions)
        {
            if (d.Size.TryGetValue(out var size))
            {
                sizes[d.Name] = size;
            }
        }
        IEnumerable<Cell> Footprint(Building b)
        {
            if (sizes.TryGetValue(b.Definition, out var size))
            {
                var r = InteriorRules.OccupiedRect(b.Cell, new Cell(size.Width, size.Height), b.Rotation);
                if (r.Width > 0 && r.Height > 0)
                {
                    return LayoutUtilityRules.RectangleCells(r);
                }
            }
            return [b.Cell];
        }
        var built = new HashSet<Cell>();
        foreach (var b in census.Buildings)
        {
            foreach (var c in b.Cells)
            {
                built.Add(c);
            }
        }
        foreach (var s in census.Sites)
        {
            if (s.Building is not { } site)
            {
                continue;
            }
            foreach (var c in Footprint(site))
            {
                built.Add(c);
            }
        }
        if (projection.Facts.ConstructionClaims.TryGetValue(out var claims))
        {
            foreach (var c in claims)
            {
                if (c.Building is { } claimed && ConstructionOwnershipRules.WorkOpen(claimed, projection.Facts.CurrentConstruction) != BuildingWork.BuildingGone)
                {
                    foreach (var cell in Footprint(claimed))
                    {
                        built.Add(cell);
                    }
                }
            }
        }
        foreach (var c in projection.Cells)
        {
            var edifice = c.PlayerEdifice();
            _ = c.Doorway.TryGetValue(out var door);
            if (edifice.Length != 0 || door)
            {
                built.Add(c.Cell);
            }
        }
        return built;
    }
}

public sealed partial class Rounder
{
    /// <summary>
    /// <see cref="Rounder.OccupiedCells"/> plus the walls of the planned rooms an open journal plan is working
    /// on (<see cref="Rounder.RoomMethodOrigins"/>). Null when the census or the plan catalog is unknown;
    /// otherwise also the interior origins of those rooms.
    /// </summary>
    internal async Task<LayoutOccupancy?> LayoutOccupiedAsync(ColonyProjection projection, LayoutPlan plan, CancellationToken ct)
    {
        var cells = Rounder.OccupiedCells(projection);
        if (cells is null)
        {
            return null;
        }
        IReadOnlyList<PlanState> plans;
        try
        {
            plans = await Player.Journal.LoadPlansAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        var origins = Rounder.RoomMethodOrigins(plans);
        var rooms = Rounder.RoomMethodIds(plans);
        foreach (var c in LayoutPlan.InFlightRoomCells(plan, origins, rooms))
        {
            cells.Add(c);
        }
        return new LayoutOccupancy(cells, origins, rooms);
    }
}
