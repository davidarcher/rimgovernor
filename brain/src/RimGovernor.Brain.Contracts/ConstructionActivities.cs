using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using Identity = RimGovernor.Contracts.Wire.Common.Identity;
using Cell = RimGovernor.Brain.Domain.Cell;
using System.Text.Json.Serialization;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>
/// Placed sites (<see cref="Buildings"/>) and hypothetical, unplaced sites (<see cref="Planned"/>). At most 16 of
/// each and 16 colonists are read; the caller batches the rest.
/// </summary>
public sealed record ConstructionActivityRequest(Identity Identity,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Building>))] EquatableArray<Building> Buildings)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedConstructionSite>))]
    public EquatableArray<PlannedConstructionSite> Planned { get; init; }
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
/// piece lists its one anchor). Work is summed over the pieces; at most 128 positions per site.
/// </summary>
public sealed record PlannedConstructionSite(string Id, string Definition, string Stuff, RimGovernor.Brain.Domain.Rotation Rotation,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Positions);
/// <summary>A position native placement rejected, with its reason.</summary>
public sealed record PlannedSiteRejection(Cell Position, string Reason);
/// <summary>A thing in a planned footprint. Reported unclassified; whether it blocks or needs clearing is the reader's to decide.</summary>
public sealed record PlannedSiteBlocker(Cell Position, string Thing, string Definition, string Category);
/// <summary>
/// A planned site's legality and per pawn x material source estimates in the placed-site option shape. An unknown
/// <see cref="CanPlace"/> (unresolved definition or stuff, or the site was not read) leaves the site unknown; a pawn
/// with no route has no row.
/// </summary>
public sealed record PlannedSiteAnswer(string Site, Fact<bool> CanPlace)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteRejection>))]
    public EquatableArray<PlannedSiteRejection> Rejections { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteBlocker>))]
    public EquatableArray<PlannedSiteBlocker> Blockers { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public EquatableArray<ConstructionActivityOption> Options { get; init; }
}
public sealed record ConstructionActivityRead
{
    public ObservationContext? Context { get; init; }
    public bool Known { get; init; }
    public bool Truncated { get; init; }
    public EquatableArray<string> Findings { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConstructionActivityOption>))]
    public EquatableArray<ConstructionActivityOption> Options { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlannedSiteAnswer>))]
    public EquatableArray<PlannedSiteAnswer> Planned { get; init; }
}
