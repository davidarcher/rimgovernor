using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Common;
using Cell = RimGovernor.Brain.Domain.Cell;
using System.Text.Json.Serialization;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Observations;

/// <summary>
/// Placed sites (<see cref="Buildings"/>), hypothetical, unplaced sites (<see cref="Planned"/>) and routes between
/// planned sites (<see cref="Routes"/>) for every free spawned colonist. Every one asked is read: nothing is capped (#3341).
/// </summary>
public sealed record ConstructionActivityRequest(RimGovernor.Brain.GameFacts.Common.Identity Identity,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Building>))] EquatableArray<Building> Buildings)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedConstructionSite>))]
    public EquatableArray<PlannedConstructionSite> Planned { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRouteProbe>))]
    public EquatableArray<PlannedRouteProbe> Routes { get; init; }
    /// <summary>Unplaced planned shelters whose thermal cells and planned heaters the read samples (#3323); null asks nothing.</summary>
    public PlannedThermalRequest? Thermal { get; init; }
    /// <summary>Planned rooms whose closed role and impressiveness the read predicts (#3339); empty asks nothing.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomShape>))]
    public EquatableArray<PlannedRoomShape> RoomPredictions { get; init; }
}
/// <summary>
/// One planned room closed in one ring stuff (#3339): its interior cells, the unbuilt ring pieces (walls, doors) its
/// planned sites would build there and its planned floors (a TerrainDef piece on an interior cell, #3797). The read counts
/// the pieces as built; every other cell and thing is as observed.
/// </summary>
public sealed record PlannedRoomShape(string Room, string Stuff,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Interior,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomPiece>))] EquatableArray<PlannedRoomPiece> Pieces)
{
    /// <summary>
    /// The planned furniture the room would hold (#3840): its Build sites (beds, end tables, heaters) in their own stuff, each
    /// counted as standing in the closed room's Wealth, Beauty, Space and Cleanliness, so the stage is the furnished room's.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomFurniture>))]
    public EquatableArray<PlannedRoomFurniture> Furniture { get; init; }
}
public sealed record PlannedRoomPiece(Cell Cell, string Definition, string Stuff);
/// <summary>One planned furniture piece (#3840): its anchor cell, def, stuff ("" for a def made from none) and rotation, the occupied rect native derives.</summary>
public sealed record PlannedRoomFurniture(Cell Cell, string Definition, string Stuff, RimGovernor.Brain.Domain.Rotation Rotation);
/// <summary>
/// One route the planned read probes per free colonist (#3313): from <see cref="From"/> (or, with
/// <see cref="FromStart"/>, from the pawn's own position) to the cell of <see cref="Reach"/> nearest that origin, or to
/// <see cref="To"/> itself when Reach is empty. The row is labelled From -> To whichever cell it reached.
/// </summary>
public sealed record PlannedRouteProbe(Cell From, Cell To,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Reach)
{
    public bool FromStart { get; init; }
    /// <summary>A prison walk under this closed room's projected pieces and furniture. Null reads the live map.</summary>
    public PlannedRoomShape? Projection { get; init; }
    /// <summary>Also read direct returns from every projected interaction endpoint to this work origin.</summary>
    public bool WorkOrigin { get; init; }
}

/// <summary>
/// One hypothetical site: <see cref="Positions"/> are the placement anchors of one piece each of
/// <see cref="Definition"/> in <see cref="Stuff"/> at <see cref="Rotation"/> (a wall run lists every wall cell; a 2x1
/// piece lists its one anchor). Work is summed over the pieces.
/// </summary>
public sealed record PlannedConstructionSite(string Id, string Definition, string Stuff, RimGovernor.Brain.Domain.Rotation Rotation,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Positions);
