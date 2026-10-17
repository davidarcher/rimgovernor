using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
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
                r.Room.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), new Cell(r.Bounds.X, r.Bounds.Z), r.Bounds.Width, r.Bounds.Height))),
            Heaters = EquatableArray.From(heaters),
        };
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
