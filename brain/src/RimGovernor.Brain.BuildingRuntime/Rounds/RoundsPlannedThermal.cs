namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// The native reads of the round's unplaced planned shelters, in one construction activity read: the thermal read
    /// (#3323; PlannedThermalRequestOf, null when no shelter qualifies) and the closed-room predictions (#3339;
    /// PlannedRoomPredictionRequestOf over the planned sites and the placed ones on a ring, #3537, none when nothing was
    /// asked). Neither is read while the planned sites are unread or native has no activity source.
    /// </summary>
    internal async Task<(O.CoupledThermalRead? Thermal, EquatableArray<O.PlannedRoomPrediction> Predictions)> PlannedShelterReadsAsync(
        GenerationSnapshot snapshot, ColonyProjection colony, IEnumerable<PlannedRoom> shelters, PlannedSiteFacts? planned, CancellationToken ct)
    {
        if (Native is not IActivityTaskSource source || planned is null)
        {
            return (null, []);
        }
        var rooms = shelters.ToList();
        var furniture = colony.Shapes.Furniture;
        var thermal = PlannedRules.PlannedThermalRequestOf(rooms, planned.Sites, [furniture.Heater, .. furniture.FueledHeaters]);
        var predictions = PlannedRules.PlannedRoomPredictionRequestOf(rooms, planned.Sites,
            colony.Facts.CurrentConstruction is { } construction ? construction.Sites : []);
        if (thermal is null && predictions.Count == 0)
        {
            return (null, []);
        }
        var read = await source.ReadConstructionActivityAsync(new O.ConstructionActivityRequest(Boundary.Identity(snapshot), [])
            { Thermal = thermal, RoomPredictions = predictions }, ct).ConfigureAwait(false);
        return (read.Thermal, read.RoomPredictions);
    }
}
