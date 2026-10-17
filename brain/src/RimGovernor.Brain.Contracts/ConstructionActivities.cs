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
}
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
/// <summary>
/// A position native placement rejected, with its reason. <see cref="Illegal"/> is true when the refusal outlasts
/// clearing every removable blocker (terrain, water, map edge, another faction's building, research): the reason is
/// then the one native gives once cleared. False: clearing (or revealing a fogged cell) removes it.
/// </summary>
public sealed record PlannedSiteRejection(Cell Position, string Reason)
{
    public bool Illegal { get; init; }
}
/// <summary>
/// A thing in a planned footprint, with its clearing <see cref="Verdict"/>. A <see cref="PlannedBlockerVerdict.Cleared"/>
/// blocker names the clearing op in <see cref="Clearing"/>; the others leave it empty.
/// </summary>
public sealed record PlannedSiteBlocker(Cell Position, string Thing, string Definition, string Category)
{
    public PlannedBlockerVerdict Verdict { get; init; }
    public string Clearing { get; init; } = "";
}
/// <summary>What clearing one footprint thing takes. Unspecified: unknown (a haulable with no accepting storage).</summary>
[JsonConverter(typeof(WireEnumConverter<PlannedBlockerVerdict>))]
public enum PlannedBlockerVerdict
{
    [WireName("PLANNED_BLOCKER_VERDICT_UNSPECIFIED")]
    Unspecified = 0,
    /// <summary>Nothing to clear: placement wipes it, it does not block construction, or it is a pawn.</summary>
    [WireName("PLANNED_BLOCKER_VERDICT_NO_CLEARING")]
    NoClearing = 1,
    /// <summary>A Cut, Haul or Mine op of the answer removes it.</summary>
    [WireName("PLANNED_BLOCKER_VERDICT_CLEARED")]
    Cleared = 2,
    /// <summary>Removable, but native cannot estimate the work (a colony building to deconstruct, a non-natural mineable).</summary>
    [WireName("PLANNED_BLOCKER_VERDICT_UNESTIMABLE")]
    Unestimable = 3,
    /// <summary>Not removable by the colony (another faction's building): the site is infeasible.</summary>
    [WireName("PLANNED_BLOCKER_VERDICT_ILLEGAL")]
    Illegal = 4,
}
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
}
/// <summary>
/// A clearing prerequisite of a planned site: one Cut op for all its plants, one Mine op for all its natural rock,
/// one Haul op per haulable stack. <see cref="Options"/> are per-pawn rows in the <see cref="ConstructionActivityOption"/>
/// shape with <c>Site</c> = <see cref="Id"/>; a pawn that cannot do it or has no route has no row.
/// </summary>
public sealed record PlannedSiteClearing(string Id, PlannedClearingKind Kind,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Cells)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public EquatableArray<ConstructionActivityOption> Options { get; init; }
}
/// <summary>
/// A planned site's legality and per pawn x material source estimates in the placed-site option shape. An unknown
/// <see cref="CanPlace"/> (unresolved definition or stuff, or the site was not read) leaves the site unknown; a pawn
/// with no route has no row. <see cref="CanPlace"/> is native legality as the footprint stands;
/// <see cref="Verdict"/> is feasibility once <see cref="Clearing"/> has run.
/// </summary>
public sealed record PlannedSiteAnswer(string Site, Fact<bool> CanPlace)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteRejection>))]
    public EquatableArray<PlannedSiteRejection> Rejections { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteBlocker>))]
    public EquatableArray<PlannedSiteBlocker> Blockers { get; init; }
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
    /// The request's route probes, one row per probe x free colonist with <c>Ticks</c> unknown where no path is found;
    /// a <see cref="PlannedRouteProbe.FromStart"/> row has <c>FromStart</c> set and <c>From</c> the pawn's cell.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CoupledRoute>))]
    public EquatableArray<CoupledRoute> Routes { get; init; }
    /// <summary>The answer to <see cref="ConstructionActivityRequest.Thermal"/>; null when nothing was asked.</summary>
    public CoupledThermalRead? Thermal { get; init; }
}
