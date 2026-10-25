using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Placement;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Placement;

public sealed record PlacementRequest
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementCandidate>))]
    public required EquatableArray<PlacementCandidate> Placements { get; init; }

    public required RimGovernor.Brain.GameFacts.Common.Identity Identity { get; init; }
}

public sealed record PlacementCandidate
{
    public required string DefName { get; init; }

    public required int X { get; init; }

    public required int Z { get; init; }

    public required RimGovernor.Brain.GameFacts.Placement.Rotation Rotation { get; init; }

    /// <summary>
    /// Empty requests the game's default material.
    /// </summary>
    public required string Stuff { get; init; }

    /// <summary>
    /// Previews the placement as though natural rock at the cell were already
    /// mined, for a planned building whose excavation the same plan orders
    /// first. Dispatch sets it false.
    /// </summary>
    public required bool IgnoreNaturalRock { get; init; }
}
