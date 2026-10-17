namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// The native thermal read of the round's unplaced planned shelters (#3323): their cells and planned heaters
    /// (PlannedThermalRequestOf), through the construction activity read. Null when no shelter qualifies, the planned
    /// sites are unknown, or native does not answer; the shelter then stays explicitly unknown.
    /// </summary>
    internal async Task<O.CoupledThermalRead?> PlannedThermalAsync(GenerationSnapshot snapshot, ColonyProjection colony,
        IEnumerable<PlannedRoom> shelters, Fact<PlannedSiteFacts> sites, CancellationToken ct)
    {
        if (Native is not IActivityTaskSource source || !sites.TryGetValue(out var planned)
            || PolicyApi.PlannedThermalRequestOf(shelters, planned.Sites, colony.Shapes.Furniture.Heater) is not { } request)
        {
            return null;
        }
        var read = await source.ReadConstructionActivityAsync(new O.ConstructionActivityRequest(Boundary.Identity(snapshot), []) { Thermal = request }, ct)
            .ConfigureAwait(false);
        return read is { Known: true } ? read.Thermal : null;
    }
}
