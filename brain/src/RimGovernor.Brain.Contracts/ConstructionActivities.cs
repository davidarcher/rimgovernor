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
/// One planned room closed in one ring stuff (#3339): its interior cells and the unbuilt ring pieces (walls, doors) its
/// planned sites would build there. The read counts the pieces as built; every other cell and thing is as observed.
/// </summary>
public sealed record PlannedRoomShape(string Room, string Stuff,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Interior,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomPiece>))] EquatableArray<PlannedRoomPiece> Pieces);
public sealed record PlannedRoomPiece(Cell Cell, string Definition, string Stuff);
/// <summary>
/// The predicted closed room (#3339): the game's RoomRoleWorkers' role ("None" when not a proper room or over 60 regions)
/// and RoomStatWorker_Impressiveness over the predicted base stats, with its RoomStatDef stage; the closed room's
/// cell count, observed open-roof cells and map-edge contact (the outdoors rules' inputs); the existing beds inside with
/// their owners, and the work tables inside whose speed stat has StatPart_WorkTableOutdoors with their current
/// room's PsychologicallyOutdoors. Unknown score or stage when a stat could not be read.
/// </summary>
public sealed record PlannedRoomPrediction(string Room, string Stuff, string Role, Fact<double> Impressiveness, Fact<int> Stage,
    int Cells, int OpenRoofCells, bool TouchesMapEdge)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Beds { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomBench>))]
    public EquatableArray<PlannedRoomBench> Benches { get; init; }
}
public sealed record PlannedRoomBench(string Bench, bool PsychologicallyOutdoors);
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
public sealed record ConstructionActivityOption(string Site, string Definition, Cell Location, string Pawn,
    string Resource, string Input, Cell Origin, int Count, int Capacity, string WorkGiver,
    Fact<double> ApproachTicks, Fact<double> DeliveryTicks, Fact<double> FinishTicks)
{
    public Fact<double> SiteApproachTicks { get; init; }
    public Fact<double> AvailableInput { get; init; }
}

/// <summary>
/// One hypothetical site: <see cref="Positions"/> are the placement anchors of one piece each of
/// <see cref="Definition"/> in <see cref="Stuff"/> at <see cref="Rotation"/> (a wall run lists every wall cell; a 2x1
/// piece lists its one anchor). Work is summed over the pieces.
/// </summary>
public sealed record PlannedConstructionSite(string Id, string Definition, string Stuff, RimGovernor.Brain.Domain.Rotation Rotation,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Positions);
/// <summary>A planned site's feasibility once its footprint is cleared. Unspecified: unknown.</summary>
[JsonConverter(typeof(WireEnumConverter<PlannedSiteVerdict>))]
public enum PlannedSiteVerdict
{
    [WireName("PLANNED_SITE_VERDICT_UNSPECIFIED")]
    Unspecified = 0,
    /// <summary>Legal once cleared, and every blocker needs no clearing or has an estimated op.</summary>
    [WireName("PLANNED_SITE_VERDICT_FEASIBLE")]
    Feasible = 1,
    /// <summary>Legal once cleared, but some blocker (or a fogged cell) has no native estimate: leave the alternative unvalued.</summary>
    [WireName("PLANNED_SITE_VERDICT_UNESTIMABLE")]
    Unestimable = 2,
    /// <summary>A non-removable reason refuses it: infeasible.</summary>
    [WireName("PLANNED_SITE_VERDICT_ILLEGAL")]
    Illegal = 3,
}
[JsonConverter(typeof(WireEnumConverter<PlannedClearingKind>))]
public enum PlannedClearingKind
{
    [WireName("PLANNED_CLEARING_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("PLANNED_CLEARING_KIND_CUT")]
    Cut = 1,
    [WireName("PLANNED_CLEARING_KIND_HAUL")]
    Haul = 2,
    [WireName("PLANNED_CLEARING_KIND_MINE")]
    Mine = 3,
    /// <summary>A building the player may deconstruct (a ruin wall, door or furniture, or the colony's own): one op per building, its Deconstruct job (#3557).</summary>
    [WireName("PLANNED_CLEARING_KIND_DECONSTRUCT")]
    Deconstruct = 4,
    /// <summary>A minifiable building of the colony's own packed where it stands: one op per building, its Uninstall job (#3612).</summary>
    [WireName("PLANNED_CLEARING_KIND_UNINSTALL")]
    Uninstall = 5,
    /// <summary>A floor removed from its cells (the FloorOut reconcile op). Declared for #3615; no read emits it yet.</summary>
    [WireName("PLANNED_CLEARING_KIND_REMOVE_FLOOR")]
    RemoveFloor = 6,
}
/// <summary>
/// A clearing op: a prerequisite of a planned site (one Cut op for all its plants, one Mine op for all its natural
/// rock, one Haul op per haulable stack, one Deconstruct op per deconstructible building), or a standing removal
/// designation (<see cref="ConstructionActivityRead.Removals"/>: one Deconstruct or Uninstall op per designated
/// building and one Haul op per designated stack, <see cref="Id"/> the thing's id). A single-thing op's rows name the
/// thing: <c>Input</c> its id, <c>Resource</c> its def. <see cref="Options"/> are per-pawn rows in the
/// <see cref="ConstructionActivityOption"/> shape with <c>Site</c> = <see cref="Id"/>; a pawn that cannot do it or has
/// no route has no row.
/// </summary>
public sealed record PlannedSiteClearing(string Id, PlannedClearingKind Kind)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public EquatableArray<ConstructionActivityOption> Options { get; init; }
    /// <summary>A set op's members (#3613): the plants a <see cref="PlannedClearingKind.Cut"/> op clears, lowest thing id
    /// first. The brain binds the op as one Work set step over them; empty for a single-building op.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Targets { get; init; }
}
/// <summary>
/// A planned site's legality and per pawn x material source estimates in the placed-site option shape. An unknown
/// <see cref="CanPlace"/> (unresolved definition or stuff, or the site was not read) leaves the site unknown; a pawn
/// with no route has no row. <see cref="CanPlace"/> is native legality as the footprint stands;
/// <see cref="Verdict"/> is feasibility once <see cref="Clearing"/> has run.
/// </summary>
public sealed record PlannedSiteAnswer(string Site, Fact<bool> CanPlace)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public EquatableArray<ConstructionActivityOption> Options { get; init; }
    public PlannedSiteVerdict Verdict { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteClearing>))]
    public EquatableArray<PlannedSiteClearing> Clearing { get; init; }
}
public sealed record ConstructionActivityRead
{
    public ObservationContext? Context { get; init; }
    public bool Known { get; init; }
    public EquatableArray<string> Findings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public EquatableArray<ConstructionActivityOption> Options { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteAnswer>))]
    public EquatableArray<PlannedSiteAnswer> Planned { get; init; }
    /// <summary>
    /// The standing removal designations as clearing ops (#3557, #3612, #3614): one <c>Deconstruct</c> op per Deconstruct
    /// designation on a building the colony may deconstruct (a reconcile-stood interior ruin or stale piece, the colony's
    /// own), one <c>Uninstall</c> op per Uninstall designation on a minifiable building of the colony's (the reconcile's
    /// Pack) and one <c>Haul</c> op per Haul designation on a spawned haulable item (the reconcile's HaulOut), <c>Id</c> the
    /// thing's id. A building's op has one row per free colonist: <c>Site</c> and <c>Input</c> the building's id,
    /// <c>Definition</c> and <c>Resource</c> its def, <c>Location</c> its cell, <c>WorkGiver</c> the removal's giver,
    /// <c>FinishTicks</c> its work; a roof-holder the enclosure guard would wait on has no op (finding
    /// <c>removal_roof_support</c>). A stack's op has the planned-clearing haul rows (<c>Origin</c> the stack's cell,
    /// <c>Location</c> the nearest cell of the best storage accepting it outside any planned footprint covering it,
    /// <c>Count</c> the stack, <c>DeliveryTicks</c> by trips); a stack with no destination has no op (finding
    /// <c>removal_haul_destination_unknown</c>).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteClearing>))]
    public EquatableArray<PlannedSiteClearing> Removals { get; init; }
    /// <summary>
    /// The request's route probes, one row per probe x free colonist with <c>Ticks</c> unknown where no path is found;
    /// a <see cref="PlannedRouteProbe.FromStart"/> row has <c>FromStart</c> set and <c>From</c> the pawn's cell.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledRoute>))]
    public EquatableArray<CoupledRoute> Routes { get; init; }
    /// <summary>The answer to <see cref="ConstructionActivityRequest.Thermal"/>; null when nothing was asked.</summary>
    public CoupledThermalRead? Thermal { get; init; }
    /// <summary>The answer to <see cref="ConstructionActivityRequest.RoomPredictions"/>, one row per asked room and stuff.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedRoomPrediction>))]
    public EquatableArray<PlannedRoomPrediction> RoomPredictions { get; init; }
}
