namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>
    /// The native reads of the round's unplaced planned shelters, in one construction activity read: the thermal read
    /// (#3323; PlannedThermalRequestOf, null when no shelter qualifies) and the closed-room predictions (#3339;
    /// PlannedRoomPredictionRequestOf over the planned sites and the placed ones on a ring, #3537, none when nothing was
    /// asked). Neither is read while the planned sites are unread or native has no activity source.
    /// </summary>
    internal async Task<(RimGovernor.Brain.GameFacts.Observations.CoupledThermalRead? Thermal, EquatableArray<RimGovernor.Brain.GameFacts.Observations.PlannedRoomPrediction> Predictions, IReadOnlyDictionary<int, EquatableArray<PlannedBuildSite>> Prerequisites)> PlannedShelterReadsAsync(
        GenerationSnapshot snapshot, ColonyReading colony, IEnumerable<PlannedRoom> shelters, PlannedSiteFacts? planned, CancellationToken ct,
        EquatableArray<RimGovernor.Brain.GameFacts.Observations.PlannedRoomPrediction>? predicted = null)
    {
        if (Native is not IActivityTaskSource source || planned is null)
        {
            return (null, [], new Dictionary<int, EquatableArray<PlannedBuildSite>>());
        }
        var rooms = shelters.ToList();
        var furniture = colony.Facts.Shapes.Furniture;
        var cooler = LayoutClusterRules.BestCooler(colony.Facts.Stations).Def;
        if (!rooms.Any(r => PlannedRoomCredits.Cold(colony.Facts.LayoutPlan, r))) cooler = "";
        else if (cooler.Length == 0) throw new RequiredFactException("catalog cooler");
        else if (!ColonyPlanningRules.DefinitionAvailable(colony.Facts, cooler)) cooler = "";
        var thermal = PlannedRules.PlannedThermalRequestOf(rooms, planned.Sites, [furniture.Heater, .. furniture.FueledHeaters], cooler, Policy.FoodStorage.FreezerTargetC, colony.Facts.LayoutPlan);
        var predictions = predicted.HasValue ? [] : PlannedRules.PlannedRoomPredictionRequestOf(rooms, planned.Sites,
            colony.Facts.CurrentConstruction.Sites, colony.Facts.LayoutPlan, colony.Facts.Filth);
        var prisonBeds = ForecastPrison.BedsOf(rooms, planned.Sites, colony.Facts.Sleeping, colony.Facts.SleepingBeds, colony.Facts.LayoutPlan);
        var origins = (colony.Facts.ForecastObserved.Coupled?.Routes ?? []).SelectMany(r => new[] { r.From, r.To })
            .Concat((colony.Facts.ForecastObserved.Needs?.Pawns ?? []).Select(p => p.Location))
            .Concat(planned.Routes.SelectMany(r => new[] { r.From, r.To })).Concat(planned.Sites.SelectMany(s => s.Positions));
        var prisonRoutes = ForecastPrison.RouteProbes(predictions, prisonBeds, origins,
            colony.Facts.ForecastObserved.Needs?.Prison);
        if (thermal is null && predictions.Count == 0)
        {
            return (null, predicted ?? [], new Dictionary<int, EquatableArray<PlannedBuildSite>>());
        }
        var read = await source.ReadConstructionActivityAsync(new RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest(Boundary.Identity(snapshot), [])
            { Thermal = thermal, RoomPredictions = predictions, Routes = prisonRoutes }, ct).ConfigureAwait(false);
        if (thermal is null) return (null, predicted ?? read.RoomPredictions, new Dictionary<int, EquatableArray<PlannedBuildSite>>());
        var prepared = FreezerPrerequisites.Of(colony.Facts, read.Thermal ?? throw new RequiredFactException("planned shelter thermal"));
        var missing = prepared.Values.SelectMany(s => s).Where(s => s.Answer is null).DistinctBy(s => s.Id).ToArray();
        var answers = missing.Length == 0 ? null : await source.ReadConstructionActivityAsync(
            new RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest(Boundary.Identity(snapshot), [])
                { Planned = PlannedRules.PlannedSiteRequest(missing) }, ct).ConfigureAwait(false);
        var joined = PlannedRules.JoinPlannedSites(missing, answers).Sites.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var prerequisites = prepared.ToDictionary(p => p.Key, p => EquatableArray.From(p.Value.Select(s => s.Answer is not null ? s
            : joined.TryGetValue(s.Id, out var site) && site.Answer is not null ? site : throw new RequiredFactException("freezer prerequisite site:" + s.Id))));
        return (read.Thermal, predicted ?? read.RoomPredictions, prerequisites);
    }
}
