using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

// The dry run of retiring OwnRows (#3487): what the owners would remove from their rooms once they reconcile
// against the full furniture list. Logged as furniture_stale_dry_run; nothing is removed while
// RoundsPolicy.RetireOwnRows is false.
public sealed partial class RoundsBuildingPlanner
{
    /// <summary>The planned roles whose furnishing is FacilityWants but whose planned-room reconcile is RingOnly with no template.</summary>
    private static readonly PlannedRole[] FacilityStaleRoles =
    [
        PlannedRole.Dining, PlannedRole.Rec, PlannedRole.Lab, PlannedRole.Workshop, PlannedRole.Kitchen, PlannedRole.Butchery,
    ];

    /// <summary>How often a facility room's furniture is read for the dry-run log: a quarter day.</summary>
    private const long FacilityStaleReadTicks = GameTime.TicksPerDay / 4;

    /// <summary>
    /// The definitions other lanes stand in owned rooms and the room's list carries over: the party spot, the
    /// lamps, the plant pot, the dining and recreation comfort pieces and every sculpture the catalog makes.
    /// </summary>
    internal Func<string, bool> CarriedOver(ColonyProjection facts)
    {
        var defs = new HashSet<string>(LightingDefinitions(), StringComparer.Ordinal) { PartySpotRules.PartySpotDefinition, RoomQualityRules.PlantPotDefinition };
        if (facts.Facts.Comfort.TryGetValue(out var comfort))
        {
            defs.UnionWith(comfort.Furniture.Definitions());
            defs.UnionWith(comfort.WatchBuildings);
            defs.Add(comfort.RecreationFoothold);
        }
        var items = facts.Facts.Items;
        return def => defs.Contains(def) || items.SculptureSize(def, out _);
    }

    /// <summary>
    /// Logs the interior furniture an owner reconciling against its full list would remove from room: one
    /// furniture_stale_dry_run row per room and definition, the first time it is seen.
    /// </summary>
    internal void NoteStaleFurniture(PlannedRoom room, string owner, IReadOnlyList<ClearanceTarget> stale)
    {
        foreach (var group in stale.GroupBy(row => row.DefName, StringComparer.Ordinal))
        {
            if (!Reviewer.StaleFurnitureLogged.Add(string.Create(CultureInfo.InvariantCulture, $"{room.Id}|{group.Key}")))
            {
                continue;
            }
            var cells = string.Join(" ", group.Select(row => row.Minimum == row.Maximum ? Cell(row.Minimum) : Cell(row.Minimum) + "-" + Cell(row.Maximum)));
            Tel.Event(TelemetryLevel.Info, "building-planner", "furniture_stale_dry_run", string.Create(CultureInfo.InvariantCulture, $"would remove {group.Key} at {cells} from room {room.Id} role {Wire.Name(room.Role)} owner {owner}"));
        }

        static string Cell(Cell c) => string.Create(CultureInfo.InvariantCulture, $"{c.X},{c.Z}");
    }

    /// <summary>
    /// The facility rooms' share of the dry run: each built dining, recreation, lab, workshop, kitchen and
    /// butchery room, its standing furniture read and listed against the role's FacilityWants. Their planned-room
    /// reconcile is RingOnly, so this read stands in for the template until they reconcile against it.
    /// </summary>
    internal async Task NoteFacilityStaleAsync(ColonyProjection facts, CancellationToken call)
    {
        if (Native is not Observation.IClearanceSource source || !facts.LayoutPlan.TryGetValue(out var plan) || !facts.Rooms.TryGetValue(out var rooms) || !facts.Facts.CurrentConstruction.TryGetValue(out var construction))
        {
            return;
        }
        var now = facts.Identity.Tick.Value;
        foreach (var room in plan.Rooms)
        {
            if (!FacilityStaleRoles.Contains(room.Role) || !PlannedRoom.CensusRoomIn(room, rooms).Ok || (Reviewer.FacilityStaleRead.TryGetValue(room.Id, out var last) && now - last < FacilityStaleReadTicks))
            {
                continue;
            }
            var (inner, ok) = InteriorRules.InteriorRoomFromLayout(room, rooms.Shapes);
            if (!ok || inner is null)
            {
                continue;
            }
            Reviewer.FacilityStaleRead[room.Id] = now;
            var template = RoomFurnishingRules.RoomFurnishing(room, null, RoomFurnishingRules.FacilityWants(inner), construction.Buildings).Pieces;
            var read = await ClearanceReads.ObserveClearanceCensusOnGround(source, facts.Identity, false, [room.RoomGround()], call).ConfigureAwait(false);
            if (!read.TryGetValue(out var census))
            {
                continue;
            }
            var (_, player) = ClearanceGroundRules.SplitGroundRows(census.Targets, facts.Facts.Preferences);
            NoteStaleFurniture(room, "facility", ReconcileRules.StaleInterior(room, player, template, null, CarriedOver(facts)));
        }
    }
}
