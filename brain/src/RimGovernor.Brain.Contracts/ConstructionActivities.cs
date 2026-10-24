using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using Identity = RimGovernor.Contracts.Wire.Common.Identity;
using Cell = RimGovernor.Brain.Domain.Cell;
using System.Text.Json.Serialization;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>
/// Placed sites (<see cref="Buildings"/>), hypothetical, unplaced sites (<see cref="Planned"/>) and routes between
/// planned sites (<see cref="Routes"/>) for every free spawned colonist. Every one asked is read: nothing is capped (#3341).
/// </summary>
public sealed record ConstructionActivityRequest(Identity Identity,
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
/// The predicted closed room (#3339): the game's RoomRoleWorkers' role ("None" when not a proper room or over 60 regions)
/// and RoomStatWorker_Impressiveness over the predicted base stats, with its RoomStatDef stage; the closed room's
/// cell count, observed open-roof cells and map-edge contact (the outdoors rules' inputs); the existing beds inside with
/// their owners, and the work tables inside whose speed stat has StatPart_WorkTableOutdoors with their current
/// room's PsychologicallyOutdoors. Unknown stage when a stat could not be read; Impressiveness is the score, the stat's
/// roomlessScore for a None room and 0 when Role is "" (unread).
/// </summary>
public sealed record PlannedRoomPrediction(string Room, string Stuff, string Role, double Impressiveness, Fact<int> Stage,
    int Cells, int OpenRoofCells, bool TouchesMapEdge)
{
    /// <summary>
    /// The distinct terrains of the closed room's cells with the shape's planned floors laid, each with its TerrainDef
    /// tags (#3808: RoomRequirement_TerrainWithTags reads every cell's), in TerrainDef name order; empty when Role is "".
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomTerrain>))]
    public EquatableArray<PlannedRoomTerrain> Terrains { get; init; }

    /// <summary>
    /// RoomStatWorker_Cleanliness of the closed room with the shape's planned floor pieces (TerrainDef pieces) laid in
    /// place of their cells' terrain and no filth beyond what stands now (#3797); the stat's roomlessScore for a None
    /// room; 0 when Role is "" (unread). Impressiveness reads the cleanliness without the planned floor.
    /// </summary>
    public double Cleanliness { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Beds { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomBench>))]
    public EquatableArray<PlannedRoomBench> Benches { get; init; }
}
public sealed record PlannedRoomBench(string Bench, bool PsychologicallyOutdoors);
/// <summary>One terrain of a predicted closed room (#3808): its TerrainDef tags.</summary>
public sealed record PlannedRoomTerrain(
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> Tags);
/// <summary>
/// One route the planned read probes per free colonist (#3313): from <see cref="From"/> (or, with
/// <see cref="FromStart"/>, from the pawn's own position) to the cell of <see cref="Reach"/> nearest that origin, or to
/// <see cref="To"/> itself when Reach is empty. The row is labelled From -> To whichever cell it reached.
/// </summary>
public sealed record PlannedRouteProbe(Cell From, Cell To,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Reach)
{
    public bool FromStart { get; init; }
}
/// <summary>
/// One pawn's row for a site or op (#3775): <see cref="SiteApproachTicks"/> is the pawn's path to the site (every row has
/// one: a pawn with no route has no row), <see cref="Delivery"/> how the pawn carries the row's stack there,
/// <see cref="Finish"/> whether it can do the site's work, <see cref="AvailableInput"/> the stack's count (0 without a
/// stack) and <see cref="Storage"/> where storage would take the stack.
/// </summary>
public sealed record ConstructionActivityOption(string Site, string Definition, Cell Location, string Pawn,
    string Resource, string Input, Cell Origin, int Count, int Capacity, string WorkGiver,
    double SiteApproachTicks, ConstructionDelivery Delivery, ConstructionFinish Finish, int AvailableInput, StackStorage Storage);

/// <summary>How a row's pawn carries its stack to the site.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConstructionDelivery.Unsourced), "unsourced")]
[JsonDerivedType(typeof(ConstructionDelivery.CannotCarry), "cannot_carry")]
[JsonDerivedType(typeof(ConstructionDelivery.Unreachable), "unreachable")]
[JsonDerivedType(typeof(ConstructionDelivery.Carried), "carried")]
public abstract record ConstructionDelivery
{
    private protected ConstructionDelivery() { }

    /// <summary>The row carries no stack: the site owes nothing, or the op is work on a thing or cells.</summary>
    public sealed record Unsourced : ConstructionDelivery;

    /// <summary>The pawn cannot haul the stack: no carry capacity for it, forbidden to the pawn, hauling or dumb labor
    /// disabled, no manipulation, or the stack cannot be reserved and reached.</summary>
    public sealed record CannotCarry : ConstructionDelivery;

    /// <summary>A leg of the carry (pawn to stack, stack to site, site back to stack) has no path.</summary>
    public sealed record Unreachable : ConstructionDelivery;

    /// <summary>The pawn's path to the stack, and the loaded carries for its share plus the returns between trips.</summary>
    public sealed record Carried(double ApproachTicks, double DeliveryTicks) : ConstructionDelivery;
}

/// <summary>Whether a row's pawn can do the site's (or op's) work.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConstructionFinish.CannotFinish), "cannot_finish")]
[JsonDerivedType(typeof(ConstructionFinish.Finishes), "finishes")]
public abstract record ConstructionFinish
{
    private protected ConstructionFinish() { }

    /// <summary>The pawn cannot do the work: no finish giver, the construction guard, capability or skill refuses it, or its
    /// work speed is zero.</summary>
    public sealed record CannotFinish : ConstructionFinish;

    /// <summary>The work's <see cref="Ticks"/> at the pawn's speed (0 for a haul, whose work is the carry), with the skill
    /// learning it pays (null when the pawn's work learns no skill to read).</summary>
    public sealed record Finishes(double Ticks, ProductiveSkillWork? SkillWork) : ConstructionFinish;
}

/// <summary>Where storage would take a row's stack (#3046).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StackStorage.Unsourced), "unsourced")]
[JsonDerivedType(typeof(StackStorage.Unaccepted), "unaccepted")]
[JsonDerivedType(typeof(StackStorage.Accepted), "accepted")]
public abstract record StackStorage
{
    private protected StackStorage() { }

    /// <summary>The row names no stack.</summary>
    public sealed record Unsourced : StackStorage;

    /// <summary>No storage on the map accepts the stack.</summary>
    public sealed record Unaccepted : StackStorage;

    /// <summary>The straight-line distance from the stack (<see cref="ConstructionActivityOption.Origin"/>) to the nearest
    /// cell of storage that accepts it: zero when the stack is stored.</summary>
    public sealed record Accepted(double Distance) : StackStorage;
}

/// <summary>
/// One hypothetical site: <see cref="Positions"/> are the placement anchors of one piece each of
/// <see cref="Definition"/> in <see cref="Stuff"/> at <see cref="Rotation"/> (a wall run lists every wall cell; a 2x1
/// piece lists its one anchor). Work is summed over the pieces.
/// </summary>
public sealed record PlannedConstructionSite(string Id, string Definition, string Stuff, RimGovernor.Brain.Domain.Rotation Rotation,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Positions);
/// <summary>A planned site's feasibility once its footprint is cleared.</summary>
[JsonConverter(typeof(WireEnumConverter<PlannedSiteVerdict>))]
public enum PlannedSiteVerdict
{
    /// <summary>Legal once cleared, and every blocker needs no clearing or has an estimated op.</summary>
    Feasible,
    /// <summary>Legal once cleared, but some blocker (or a fogged cell) has no native estimate, or a footprint stack has no
    /// storage outside it, or a roof-holder's support cannot be read: leave the alternative unvalued.</summary>
    Unestimable,
    /// <summary>A non-removable reason refuses it: infeasible.</summary>
    Illegal,
    /// <summary>Native cannot resolve the site (no positions, an unresolved definition or stuff, a cell off the map,
    /// unreadable costs): no rows and no clearing, its finding says which.</summary>
    Unresolved,
}
[JsonConverter(typeof(WireEnumConverter<PlannedClearingKind>))]
public enum PlannedClearingKind
{
    Cut,
    Haul,
    Mine,
    /// <summary>A building the player may deconstruct (a ruin wall, door or furniture, or the colony's own): one op per building, its Deconstruct job (#3557).</summary>
    Deconstruct,
    /// <summary>A minifiable building of the colony's own packed where it stands: one op per building, its Uninstall job (#3612).</summary>
    Uninstall,
    /// <summary>A constructed floor removed from a wave of cells (the FloorOut reconcile op's RemoveFloor designations): one op per
    /// contiguous wave, its RemoveFloor job per cell (#3615).</summary>
    RemoveFloor,
}
/// <summary>
/// A clearing op: a prerequisite of a planned site (one Cut op for all its plants, one Mine op for all its natural
/// rock, one Haul op per haulable stack, one Deconstruct op per deconstructible building), or a standing removal
/// designation (<see cref="ConstructionActivityRead.Removals"/>: one Deconstruct or Uninstall op per designated
/// building, one Haul op per designated stack, <see cref="Id"/> the thing's id, one Cut op per wave of designated plants
/// and one RemoveFloor op per wave of designated cells). A single-thing op's rows name the
/// thing: <c>Input</c> its id, <c>Resource</c> its def. <see cref="Options"/> are per-pawn rows in the
/// <see cref="ConstructionActivityOption"/> shape with <c>Site</c> = <see cref="Id"/>; a pawn that cannot do it or has
/// no route has no row.
/// </summary>
public sealed record PlannedSiteClearing(string Id, PlannedClearingKind Kind)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public required EquatableArray<ConstructionActivityOption> Options { get; init; }
    /// <summary>A set op's members: the plants a <see cref="PlannedClearingKind.Cut"/> op clears (#3613) or the rock a
    /// <see cref="PlannedClearingKind.Mine"/> op mines (#3616), lowest thing id first. The brain binds the op as one Work
    /// set step over them; empty for a single-building op.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Targets { get; init; }
    /// <summary>A cell set op's members (#3615): the cells a <see cref="PlannedClearingKind.RemoveFloor"/> op clears, lowest x
    /// then z first, bound as one Work set step over them. For a roof-holder's <see cref="PlannedClearingKind.Deconstruct"/>
    /// op (#3617): the roofed cells its removal leaves unsupported, nearest the building first; the brain removes their roof
    /// (Work/RemoveRoof cell set step) before the Deconstruct in the same order and the rows' <c>Finish</c> prices both
    /// works. Empty for any other op, or a building whose removal moves no roof.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))]
    public required EquatableArray<Cell> Cells { get; init; }
}
/// <summary>
/// A planned site's feasibility and per pawn x material source estimates in the placed-site option shape. A site native
/// cannot resolve is <see cref="PlannedSiteVerdict.Unresolved"/> with no rows and no clearing; a pawn with no route has
/// no row. <see cref="Verdict"/> is feasibility once <see cref="Clearing"/> has run.
/// </summary>
public sealed record PlannedSiteAnswer(string Site)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public required EquatableArray<ConstructionActivityOption> Options { get; init; }
    public required PlannedSiteVerdict Verdict { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteClearing>))]
    public required EquatableArray<PlannedSiteClearing> Clearing { get; init; }
}
public sealed record ConstructionActivityRead
{
    public required ObservationContext Context { get; init; }
    public required EquatableArray<string> Findings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public required EquatableArray<ConstructionActivityOption> Options { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteAnswer>))]
    public required EquatableArray<PlannedSiteAnswer> Planned { get; init; }
    /// <summary>
    /// The standing removal designations as clearing ops (#3557, #3612, #3614): one <c>Deconstruct</c> op per Deconstruct
    /// designation on a building the colony may deconstruct (a reconcile-stood interior ruin or stale piece, the colony's
    /// own), one <c>Uninstall</c> op per Uninstall designation on a minifiable building of the colony's (the reconcile's
    /// Pack) and one <c>Haul</c> op per Haul designation on a spawned haulable item or open haul-out record on an ordinary
    /// stack still at its cell (the reconcile's HaulOut: a chunk takes the designation, an ordinary stack the record, #3625), <c>Id</c> the
    /// thing's id. A building's op has one row per free colonist: <c>Site</c> and <c>Input</c> the building's id,
    /// <c>Definition</c> and <c>Resource</c> its def, <c>Location</c> its cell, <c>WorkGiver</c> the removal's giver,
    /// <c>Finish</c> its work; a roof-holder's Deconstruct op carries the roofed cells its removal leaves unsupported in
    /// <c>Cells</c> (#3617), its rows priced for the roof's removal too and located at the roof cell nearest the pawn; a
    /// roof-holder whose support is unknown (or an Uninstall over one the guard would wait on) has no op (finding
    /// <c>removal_roof_support</c>). A stack's op has the planned-clearing haul rows (<c>Origin</c> the stack's cell,
    /// <c>Location</c> the nearest cell of the best storage accepting it outside any planned footprint covering it,
    /// <c>Count</c> the stack, <c>Delivery</c> carried by trips); a stack with no destination has no op (finding
    /// <c>removal_haul_destination_unknown</c>).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteClearing>))]
    public required EquatableArray<PlannedSiteClearing> Removals { get; init; }
    /// <summary>
    /// The request's route probes, one row per probe x free colonist with <c>Ticks</c> unknown where no path is found;
    /// a <see cref="PlannedRouteProbe.FromStart"/> row has <c>FromStart</c> set and <c>From</c> the pawn's cell. Unasked, a
    /// placed site's multi-stack source set (#3673) adds per row the route from the site's <c>Location</c> back to the
    /// stack's <c>Origin</c>: the leg between delivering one stack and fetching the next.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledRoute>))]
    public required EquatableArray<CoupledRoute> Routes { get; init; }
    /// <summary>The answer to <see cref="ConstructionActivityRequest.Thermal"/>; null when nothing was asked.</summary>
    public required CoupledThermalRead? Thermal { get; init; }
    /// <summary>The answer to <see cref="ConstructionActivityRequest.RoomPredictions"/>, one row per asked room and stuff.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomPrediction>))]
    public required EquatableArray<PlannedRoomPrediction> RoomPredictions { get; init; }
}
