namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// The native reads of the round's unplaced planned shelters, in one construction activity read: the thermal read
    /// (#3323; PlannedThermalRequestOf, null when no shelter qualifies) and the closed-room predictions (#3339;
    /// PlannedRoomPredictionRequestOf, unknown when nothing was asked). Both stay unknown when the planned sites are unknown
    /// or native does not answer.
    /// </summary>
    internal async Task<(O.CoupledThermalRead? Thermal, Fact<EquatableArray<O.PlannedRoomPrediction>> Predictions)> PlannedShelterReadsAsync(
        GenerationSnapshot snapshot, ColonyProjection colony, IEnumerable<PlannedRoom> shelters, Fact<PlannedSiteFacts> sites, CancellationToken ct)
    {
        if (Native is not IActivityTaskSource source || !sites.TryGetValue(out var planned))
        {
            return (null, default);
        }
        var rooms = shelters.ToList();
        var furniture = colony.Shapes.Furniture;
        var thermal = PlannedRules.PlannedThermalRequestOf(rooms, planned.Sites, [furniture.Heater, .. furniture.FueledHeaters]);
        var predictions = PlannedRules.PlannedRoomPredictionRequestOf(rooms, planned.Sites);
        if (thermal is null && predictions.Count == 0)
        {
            return (null, default);
        }
        var read = await source.ReadConstructionActivityAsync(new O.ConstructionActivityRequest(Boundary.Identity(snapshot), [])
            { Thermal = thermal, RoomPredictions = predictions }, ct).ConfigureAwait(false);
        if (read is not { Known: true })
        {
            return (null, default);
        }
        return (read.Thermal, predictions.Count == 0 ? default : Known(read.RoomPredictions));
    }
}
