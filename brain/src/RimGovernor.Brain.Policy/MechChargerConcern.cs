namespace RimGovernor.Brain.Policy;

/// <summary>What siting a charger reads from the planning window: its cells, the zone ids that are growing fields, the room census and the wastepack disposal (atomizer) cells.</summary>
public sealed record MechChargerSiteFacts
{
    public Bounds Bounds { get; init; }
    public IReadOnlyList<SiteCell> Cells { get; init; } = [];
    public IReadOnlyDictionary<string, bool> FieldZones { get; init; } = new Dictionary<string, bool>();
    public Fact<RoomObservation> Rooms { get; init; }
    public IReadOnlyList<Cell> Disposal { get; init; } = [];
}

/// <summary>
/// Ports <c>mech_charger_concern.go</c>. EnsureMechCharger keeps a recharging station free for the mechs. A
/// mechanitor's mechs recharge one at a time on a Building_MechCharger (MechChargerOwed): the goal is in deficit
/// while the colony owes one more charger, and settles once a charger stands idle. It is a Standard whose target
/// is no outstanding work, like ManagePollution. The method is one charger construction; powering it is the power
/// goal's, emptying its waste is ManagePollution's.
/// The charger is a building that produces wastepacks, so it is sited by the polluting-machine rule
/// (PollutionSites) over free ground, never by a definition name: the catalog's mech_charger flag finds the
/// definition.
/// </summary>
public static partial class PolicyFunctions
{
    public static readonly ConcernId EnsureMechCharger = new("EnsureMechCharger");

    /// <summary>RoundsFacts.MechChargerOwed's value: whether the colony owes one more charger, unknown while the mechs or the charger list are unread.</summary>
    public static Fact<bool> MechChargerNeed(Fact<MechFleet> fleet, Fact<IReadOnlyList<MechCharger>> chargers)
    {
        var fleetKnown = fleet.TryGetValue(out var f);
        if (!chargers.IsKnown || !fleetKnown)
        {
            return Unknown<bool>();
        }
        return Known(MechChargerOwed(f!.Mechanitors.Count, chargers));
    }

    /// <summary>
    /// Ranks every width x height footprint lying wholly on known free ground (walkable, unoccupied, covered by no
    /// zone, not a doorway) by PollutionSites: far from field cells, bedroom and barracks cells, dining and
    /// recreation room cells and polluted cells, then near the disposal. An unknown room census leaves the room
    /// avoid sets empty; an unknown cell verdict is not free ground.
    /// </summary>
    public static IReadOnlyList<PollutionSite> MechChargerSites(MechChargerSiteFacts f, int width, int height)
    {
        var free = new Dictionary<Cell, bool>();
        var fieldCells = new List<Cell>();
        var pollutedCells = new List<Cell>();
        var bedroomCells = new List<Cell>();
        var livingCells = new List<Cell>();
        foreach (var c in f.Cells)
        {
            var zk = c.Zone.TryGetValue(out var zone);
            if (c.ZoneId.TryGetValue(out var id) && zk && zone && f.FieldZones.GetValueOrDefault(id))
            {
                fieldCells.Add(c.Cell);
            }
            if (c.Polluted.TryGetValue(out var polluted) && polluted)
            {
                pollutedCells.Add(c.Cell);
            }
            var wk = c.Walkable.TryGetValue(out var walkable);
            var dk = c.Doorway.TryGetValue(out var door);
            free[c.Cell] = wk && walkable && !c.Occupied() && zk && !zone && dk && !door;
        }
        if (f.Rooms.TryGetValue(out var rooms))
        {
            foreach (var room in rooms.Rooms)
            {
                if (!room.Role.TryGetValue(out var role))
                {
                    continue;
                }
                if (role == RoomRole.Bedroom || role == RoomRole.Barracks)
                {
                    bedroomCells.AddRange(room.Cells);
                }
                else if (role == RoomRole.DiningRoom || role == RoomRole.RecRoom)
                {
                    livingCells.AddRange(room.Cells);
                }
            }
        }
        var corners = free.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        corners.Sort((a, b) => CellLess(a, b) ? -1 : CellLess(b, a) ? 1 : 0);
        var candidates = new List<Rectangle>();
        foreach (var c in corners)
        {
            var r = new Rectangle { X = c.X, Z = c.Z, Width = width, Height = height };
            if (r.X + r.Width > f.Bounds.Width || r.Z + r.Height > f.Bounds.Height)
            {
                continue;
            }
            var whole = true;
            foreach (var cell in RectCells(r))
            {
                whole = whole && free.GetValueOrDefault(cell);
            }
            if (whole)
            {
                candidates.Add(r);
            }
        }
        var req = new PollutionSiteRequest
        {
            Bounds = f.Bounds,
            Disposal = f.Disposal,
            FieldCells = fieldCells,
            PollutedCells = pollutedCells,
            BedroomCells = bedroomCells,
            LivingCells = livingCells,
            Candidates = candidates,
        };
        return PollutionSites(req);
    }
}
