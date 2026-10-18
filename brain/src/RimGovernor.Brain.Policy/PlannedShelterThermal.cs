using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static partial class PlannedRules
{
    /// <summary>Planned shelters one planned thermal read samples: the coupled shelter generator's own room bound.</summary>
    public const int PlannedThermalRooms = 4;

    /// <summary>The widest planned shelter side the read samples: a wider room's roof is unsupported anyway.</summary>
    public const int PlannedThermalSide = 10;

    /// <summary>
    /// The native planned thermal read for unplaced planned shelters (#3323): the enclosed rectangular shelters that still
    /// owe a planned site (rooms, lowest id first, at most PlannedThermalRooms, sides at most PlannedThermalSide), and every planned heater site of
    /// theirs, a Build site of the catalog heater def (heater). Native samples each room's interior and ring cells and
    /// the heater def at its planned cell. Null when no room qualifies.
    /// </summary>
    public static PlannedThermalRequest? PlannedThermalRequestOf(IEnumerable<PlannedRoom> rooms, IEnumerable<PlannedBuildSite> sites, string heater)
    {
        var owed = sites.Where(s => s.Zone.Length == 0).Select(s => s.Room).ToHashSet();
        var chosen = rooms.Where(r => !r.Outdoor && r.Id > 0 && owed.Contains(r.Id))
            .Select(r => (Room: r, Bounds: r.Region.Bounds(), Cells: r.Region.Cells().Count))
            .Where(r => r.Cells > 0 && r.Bounds.Width * r.Bounds.Height == r.Cells
                && r.Bounds.Width <= PlannedThermalSide && r.Bounds.Height <= PlannedThermalSide)
            .OrderBy(r => r.Room.Id).Take(PlannedThermalRooms).ToList();
        if (chosen.Count == 0)
        {
            return null;
        }
        var ids = chosen.Select(r => r.Room.Id).ToHashSet();
        IEnumerable<PlannedThermalHeater> heaters = heater.Length == 0 ? [] : sites
            .Where(s => ids.Contains(s.Room) && s.Zone.Length == 0 && s.Op == OpKind.Build && s.Definition == heater && s.Positions.Count == 1)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => new PlannedThermalHeater(s.Id, s.Definition, s.Positions[0]));
        return new PlannedThermalRequest
        {
            Rooms = EquatableArray.From(chosen.Select(r => new PlannedThermalRoom(
                new Cell(r.Bounds.X, r.Bounds.Z), r.Bounds.Width, r.Bounds.Height))),
            Heaters = EquatableArray.From(heaters),
        };
    }

    /// <summary>
    /// The closed-room predictions one round asks native for (#3339): every enclosed planned room that still owes a wall or
    /// door site, once per ring stuff (PlannedRoomAlternatives' stuffs), its interior the room's region and its pieces the
    /// cells of its wall and door sites in that stuff (a stuff-less ring site in every stuff). Nothing is capped (#3341).
    /// </summary>
    public static EquatableArray<PlannedRoomShape> PlannedRoomPredictionRequestOf(IEnumerable<PlannedRoom> rooms, IEnumerable<PlannedBuildSite> sites)
    {
        var shapes = new List<PlannedRoomShape>();
        var closing = sites.Where(s => s.Room > 0 && s.Zone.Length == 0 && s.Op is OpKind.WallIn or OpKind.DoorIn).ToLookup(s => s.Room);
        foreach (var room in rooms.Where(r => !r.Outdoor && r.Id > 0 && closing.Contains(r.Id)).OrderBy(r => r.Id))
        {
            var ring = closing[room.Id].ToList();
            var interior = EquatableArray.From(room.Region.Cells().OrderBy(c => c.X).ThenBy(c => c.Z));
            foreach (var stuff in RingStuffs(ring))
            {
                var pieces = ring.Where(s => s.Stuff.Length == 0 || s.Stuff == stuff)
                    .SelectMany(s => s.Positions.Select(p => new PlannedRoomPiece(p, s.Definition, s.Stuff)))
                    .OrderBy(p => p.Cell.X).ThenBy(p => p.Cell.Z);
                shapes.Add(new PlannedRoomShape(room.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), stuff, interior, EquatableArray.From(pieces)));
            }
        }
        return EquatableArray.From(shapes);
    }

    /// <summary>A planned room's ring stuffs in read order; one "" when its ring sites name none.</summary>
    internal static IReadOnlyList<string> RingStuffs(IEnumerable<PlannedBuildSite> ring)
    {
        var stuffs = ring.Where(s => s.Stuff.Length > 0).Select(s => s.Stuff).Distinct().ToList();
        return stuffs.Count == 0 ? [""] : stuffs;
    }

    /// <summary>
    /// The coupled thermal read with an unplaced shelter's planned read joined in (#3323): the coupled capture keeps
    /// every cell and source it has; planned rows and planned heaters fill only what it lacks. The planned read comes
    /// from the round's construction activity read, a later frame than the coupled capture. Its truncation carries over.
    /// </summary>
    public static CoupledThermalRead? WithPlannedThermal(CoupledThermalRead? observed, CoupledThermalRead? planned)
    {
        if (planned is null)
        {
            return observed;
        }
        if (observed is null)
        {
            return planned;
        }
        var cells = observed.Cells.Select(c => c.Cell).ToHashSet();
        var targets = observed.Sources.Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
        return observed with
        {
            Cells = EquatableArray.From(observed.Cells.Concat(planned.Cells.Where(c => !cells.Contains(c.Cell)))),
            Sources = EquatableArray.From(observed.Sources.Concat(planned.Sources.Where(s => !targets.Contains(s.Target)))),
        };
    }
}
