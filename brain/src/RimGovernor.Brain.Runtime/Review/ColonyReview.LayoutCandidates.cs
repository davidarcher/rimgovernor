using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>Rebuilds intra-room visits from current demand even after reload; saved inter-room trips cannot reconstruct them.</summary>
    internal async Task<IReadOnlyDictionary<int, double>> RoomSleepDisturbanceAsync(GenerationSnapshot snapshot, ColonyReading colony,
        DefinitionCatalog catalog, DerivedDemand demand, CancellationToken ct)
    {
        if (colony.Facts.LayoutPlan is not { } plan) return new Dictionary<int, double>();
        var facts = StationFactsBuilder.GraphFactsOf(colony, plan, demand.Needs, WorkLedgerView()) with
        {
            Crops = FieldCrops.Of(colony), FoodReserveDays = Seasonal(colony.Facts).FoodTargetDays,
            Child = LayoutChildRoomsRules.ChildRoomShapes(SleepingUpkeepPlanner.ChildRoomNeeds(colony), SleepingUpkeepPlanner.FurnitureDefinitions(colony)),
            Suites = SleepingUpkeepPlanner.SuiteWishesOf(colony, plan) is { } wishes ? [.. wishes] : [],
        };
        facts = StationFactsBuilder.StationFacts(catalog, facts);
        facts = facts with { HostBuild = await HostBuildAsync(snapshot, colony, facts, ct).ConfigureAwait(false) };
        return PlannedRoomCredits.SleepDisturbanceOf(plan, StationGraph.BuildStationGraph(facts.GraphInput()), facts.Catalog, facts.Rest);
    }

    /// <summary>A trial's geometry is retained until the winning grouping is adopted; nothing is persisted during comparison.</summary>
    internal sealed record TiledLayoutCandidate(RegroupCandidate Candidate, LayoutPlan Plan, StationAdvice Advice);

    internal sealed record EvaluatedLayoutCandidate(LayoutPlan Plan, StationAdvice Advice, ColonyReading Colony, ColonyModelReview Model);

    internal async Task<EvaluatedLayoutCandidate> CompareLayoutCandidatesAsync(
        GenerationSnapshot snapshot, ColonyReading colony, StationFacts facts,
        Func<StationAdvice, (LayoutPlan? Plan, StationAdvice Advice)> tile,
        Latches latches, GoalsFacts? goals, StockpileRequest? stockpile, ProductionBatchCensus batches, WorkActionCensus actions, CancellationToken ct)
    {
        var retained = new List<TiledLayoutCandidate>();
        var evaluated = new Dictionary<string, (ColonyReading Colony, ColonyModelReview Model)>(StringComparer.Ordinal);
        var prices = new Dictionary<Resource, ConstructionMaterialValue>(colony.Facts.CoupledAlternatives?.Context.MaterialValues
            ?? throw new RequiredFactException("layout_candidate_model"));
        var regrouped = await LayoutRegroupRules.RegroupAsync(LayoutStationPlanRules.RegroupInputOf(facts), async (candidates, token) =>
        {
            var tiled = new List<TiledLayoutCandidate>();
            var values = new List<RegroupValuation>();
            foreach (var candidate in candidates)
            {
                try
                {
                    var (plan, advice) = tile(LayoutStationPlanRules.AdviseStations(facts, candidate.Layout));
                    if (plan is null) values.Add(new RegroupValuation.Unplaceable(candidate.Id, "no_core"));
                    else tiled.Add(new(candidate, plan, advice));
                }
                catch (Exception e) when (e is TileException or ReplanEmptyException)
                {
                    values.Add(new RegroupValuation.Unplaceable(candidate.Id, e is ReplanEmptyException ? "no_core" : "tile_unplaceable"));
                }
            }
            var projections = await ReadLayoutCandidateBatchAsync(snapshot, colony, tiled, stockpile, token).ConfigureAwait(false);
            var generated = projections.Select(p => (p.Candidate, Colony: p.Colony,
                Coupled: ForecastIncumbentPreparation.PrepareCoupledIncumbents(p.Colony.Facts, batches, actions).Facts.CoupledAlternatives
                    ?? throw new RequiredFactException("layout_candidate_coupled:" + p.Candidate.Candidate.Id))).ToArray();
            // Every candidate shares one observation frame and one material-price read per distinct resource in this batch.
            var materials = await MaterialValuesAsync(snapshot, generated.SelectMany(p => ForecastValue.ChargedMaterials(p.Coupled.Context)).Where(r => !prices.ContainsKey(r)),
                colony.Facts.Stations, colony.Facts.Items, token).ConfigureAwait(false);
            foreach (var material in materials) prices.Add(material.Key, material.Value);
            foreach (var trial in generated)
            {
                var basis = colony.Facts.CoupledAlternatives?.Context ?? throw new RequiredFactException("layout_candidate_model");
                var candidateContext = trial.Coupled.Context with { RoomSleepDisturbance = PlannedRoomCredits.SleepDisturbanceOf(trial.Candidate.Advice) };
                var model = ReviewColonyModel.Review(trial.Colony, candidateContext, goals);
                if (model.Basis is null) throw new RequiredFactException("layout_candidate_model:" + string.Join(';', model.Unknowns));
                var findings = new SortedSet<string>(trial.Coupled.Context.Findings, StringComparer.Ordinal);
                var (occupants, royal) = SleepingUpkeepPlanner.PlannedOccupantsOf(trial.Colony, findings);
                var context = candidateContext with
                {
                    ModelBasis = model.Basis, Investment = model.Marginals.PerUnit, SkillRaises = model.Marginals.SkillRaises,
                    MaterialValues = prices, InvestmentHorizonDays = model.HorizonDays, Tending = model.Tending, Bed = basis.Bed,
                    Joiners = new() { Roots = [.. trial.Coupled.Alternatives, .. trial.Coupled.Incumbents], Work = trial.Colony.Facts.WorkSpeeds, Food = trial.Colony.Facts.FoodWork },
                    FoodHaul = FoodHaulRules.Legs(trial.Candidate.Plan, trial.Colony.Facts.FoodWork),
                    BedroomOccupants = occupants, RoyalRooms = royal,
                    DeathrestFurniture = SleepingUpkeepPlanner.DeathrestFurnitureOf(trial.Colony),
                    ContainmentCells = SleepingUpkeepPlanner.ContainmentCellsOf(trial.Colony), CreepJoiners = basis.CreepJoiners,
                    OpenRooms = trial.Candidate.Plan.Rooms.Where(r => BuildingPlanner.PlannedRoomStartedOf(trial.Colony, trial.Candidate.Plan, r) == true
                        && !DoneRooms.Get(snapshot).Contains(r.Id)).Select(r => r.Id).ToHashSet(),
                    BlockedItems = BuildingPlanner.BlockedRoomItems(trial.Colony, trial.Coupled.Context), Findings = EquatableArray.From(findings),
                    GoalValues = GoalRules.Priced(trial.Coupled.Context.GoalValues, model.Marginals.Goals),
                };
                var coupled = trial.Coupled with { Context = context };
                var projected = trial.Colony.Facts with { CoupledAlternatives = coupled, ClothingStuff = model.ClothingStuff,
                    RoomSleepDisturbance = context.RoomSleepDisturbance };
                var request = ForecastCoupled.CoupledForecastRequestOf(new(projected.Tick.Value), ForecastCoupled.TierOneHorizonDays, projected, ResourceRules.ResourceDemandOf(projected, Policy, latches), []);
                var compared = ForecastCoupledSelection.CompareActivityOutcomes(request, coupled);
                if (compared.SelectedVector.Value.Ledger.Total is not LedgerTerm.Priced)
                    throw new RequiredFactException("layout_candidate_ledger:" + trial.Candidate.Candidate.Id);
                values.Add(new RegroupValuation.Priced(trial.Candidate.Candidate.Id, compared.SelectedVector,
                    compared.SelectedVector.Value.Ledger.Findings));
                evaluated.Add(trial.Candidate.Candidate.Id, (trial.Colony with { Facts = projected }, model));
            }
            retained.AddRange(tiled);
            return values;
        }, ct).ConfigureAwait(false);
        var winner = retained.SingleOrDefault(t => ReferenceEquals(t.Candidate.Layout.Embedding, regrouped.Embedding))
            ?? throw new ReplanEmptyException();
        var priced = evaluated[winner.Candidate.Id];
        return new(winner.Plan, winner.Advice with { Regroup = regrouped.Decisions }, priced.Colony, priced.Model);
    }

    /// <summary>Projects the full plan, including its station pieces, into the same native observation; derived room facts are rebuilt.</summary>
    internal static ColonyReading ProjectLayoutCandidate(ColonyReading colony, LayoutPlan plan, StockpileRequest? stockpile)
    {
        var rooms = plan.Rooms;
        return colony with { Facts = colony.Facts with
        {
            LayoutPlan = plan, PlannedShelters = EquatableArray.From(rooms.Where(r => !r.Outdoor)), PlannedLinks = EquatableArray.From(plan.Links),
            PlannedPens = rooms.Where(r => r.Role == PlannedRole.Pen && r.Id > 0).DistinctBy(r => r.Id).ToDictionary(r => r.Id, r => r.Herd),
            PlannedStoreStock = stockpile is null ? null : StoreRules.PlannedStoreStock(stockpile, rooms, PlannedRoomCredits.FunctionsOf(colony.Facts with { LayoutPlan = plan })),
            PlannedSites = null, PlannedRoomPredictions = [], PlannedThermal = null,
            RoomSleepDisturbance = new Dictionary<int, double>(),
            RoomPrerequisites = new Dictionary<int, EquatableArray<PlannedBuildSite>>(), CoupledAlternatives = null,
        } };
    }

    internal sealed record LayoutCandidateReading(TiledLayoutCandidate Candidate, ColonyReading Colony);

    /// <summary>One native site batch, then one route and closed-room batch for each search round. Candidate ids disambiguate identical room ids.</summary>
    internal async Task<IReadOnlyList<LayoutCandidateReading>> ReadLayoutCandidateBatchAsync(GenerationSnapshot snapshot, ColonyReading colony,
        IReadOnlyList<TiledLayoutCandidate> candidates, StockpileRequest? stockpile, CancellationToken ct)
    {
        if (candidates.Count == 0) return [];
        if (Native is not IActivityTaskSource source) throw new RequiredFactException("layout_candidate_native");
        var prepared = candidates.Select(c =>
        {
            var projected = ProjectLayoutCandidate(colony, c.Plan, stockpile);
            projected = projected with { Facts = projected.Facts with
            {
                TitleClaimQuests = [.. SleepingUpkeepPlanner.TitleClaimQuests(projected)],
                BedroomsOwed = SleepingUpkeepPlanner.BedroomsOwedOf(projected), ButcherTableAffordable = FacilityPlanningRules.ButcherTableAffordable(projected.Facts),
                WarmRooms = BurialFacts.WarmCoolingRooms(projected), MealClosetOwed = BurialFacts.MealClosetOwed(projected),
                CampfireRetireOwed = FacilityPlanningRules.CampfireRetireOwed(projected.Facts, nameof(RimWorld.ThingDefOf.Campfire)), TemperatureOwed = FacilityPlanningRules.TemperatureOwed(projected.Facts),
            } };
            if (Needs.Contains(ShelterFamily.SafeArea)) projected = projected with { Facts = projected.Facts with
                { SafeAreaOwed = SafeArea.Review(StockpilePlanner.StockpileWorld(snapshot), projected) } };
            if (Needs.Contains(RoomsFamily.Art)) projected = projected with { Facts = projected.Facts with
                { SculptureRoomsOwed = SleepingUpkeepPlanner.SculptureRoomsOwedOf(projected) } };
            var owed = PlannedOwedOf(projected, Policy.Flooring) ?? throw new RequiredFactException("layout_candidate_construction");
            projected = projected with { Facts = projected.Facts with { PlannedConstruction = PlannedConstructionOf(projected, owed) } };
            var sites = BlockedPiecesOf(projected, PlannedRoomSitesOf(projected, owed)
                ?? throw new RequiredFactException("layout_candidate_sites")).ToArray();
            return (Candidate: c, Colony: projected, Owed: owed, Sites: sites, NativeSites: PlannedRules.PlannedSiteRequest(sites));
        }).ToArray();
        var requested = prepared.SelectMany(p => p.NativeSites.Select(s => s with { Id = CandidateKey(p.Candidate.Candidate.Id, s.Id) })).ToArray();
        // Construction estimates read the live map at this footprint; identical footprints across trials need only one answer.
        var siteGroups = requested.GroupBy(s => (s.Definition, s.Stuff, s.Rotation, s.Positions)).ToArray();
        var representative = siteGroups.SelectMany(g => g.Select(s => (s.Id, Asked: g.First().Id))).ToDictionary(s => s.Id, s => s.Asked, StringComparer.Ordinal);
        var siteRead = await source.ReadConstructionActivityAsync(new(Boundary.Identity(snapshot), [])
        {
            Planned = EquatableArray.From(siteGroups.Select(g => g.First())),
        }, ct).ConfigureAwait(false);
        var answered = siteRead.Planned.ToDictionary(s => s.Site, StringComparer.Ordinal);
        var joined = prepared.Select(p =>
        {
            var answer = siteRead with { Planned = EquatableArray.From(p.NativeSites.Select(s =>
                answered.TryGetValue(representative[CandidateKey(p.Candidate.Candidate.Id, s.Id)], out var read)
                    ? read with { Site = s.Id, Options = EquatableArray.From(read.Options.Select(o => o with { Site = s.Id })) } : null).OfType<PlannedSiteAnswer>()) };
            var sites = PlannedRules.JoinPlannedSites(p.Sites, answer) with
            { Roles = EquatableArray.From(p.Owed.Sited.Select(r => (Room: r.Room.Id, r.Room.Role)).Distinct().OrderBy(r => r.Room)) };
            var targets = (p.Colony.Facts.ForecastObserved.Needs?.Pawns ?? []).SelectMany(n => n.Targets).Select(t => t.Location).ToHashSet();
            var routes = PlannedRules.PlannedRouteProbes(sites.Sites, targets);
            var shapes = PlannedRules.PlannedRoomPredictionRequestOf(p.Colony.Facts.PlannedShelters, sites.Sites,
                p.Colony.Facts.CurrentConstruction.Sites, p.Colony.Facts.LayoutPlan, p.Colony.Facts.Filth);
            var beds = ForecastPrison.BedsOf(p.Colony.Facts.PlannedShelters, sites.Sites, p.Colony.Facts.Sleeping, p.Colony.Facts.SleepingBeds, p.Colony.Facts.LayoutPlan);
            var origins = (p.Colony.Facts.ForecastObserved.Coupled?.Routes ?? []).SelectMany(r => new[] { r.From, r.To })
                .Concat((p.Colony.Facts.ForecastObserved.Needs?.Pawns ?? []).Select(n => n.Location))
                .Concat(routes.SelectMany(r => new[] { r.From, r.To })).Concat(sites.Sites.SelectMany(s => s.Positions));
            var prisonRoutes = ForecastPrison.RouteProbes(shapes, beds, origins, p.Colony.Facts.ForecastObserved.Needs?.Prison);
            return (p.Candidate, p.Colony, Sites: sites, Routes: routes, Shapes: shapes, PrisonRoutes: prisonRoutes);
        }).ToArray();
        var closureRead = await source.ReadConstructionActivityAsync(new(Boundary.Identity(snapshot), [])
        {
            RoomPredictions = EquatableArray.From(joined.SelectMany(p => p.Shapes.Select(s => s with { Room = CandidateKey(p.Candidate.Candidate.Id, s.Room) }))),
            Routes = EquatableArray.From(joined.SelectMany(p => p.Routes.Concat(p.PrisonRoutes.Select(r => r with
            { Projection = r.Projection is { } shape ? shape with { Room = CandidateKey(p.Candidate.Candidate.Id, shape.Room) } : null }))).Distinct()),
        }, ct).ConfigureAwait(false);
        var result = new List<LayoutCandidateReading>();
        foreach (var p in joined)
        {
            var predictions = CandidatePredictions(p.Candidate.Candidate.Id, closureRead.RoomPredictions);
            var sites = PlannedRules.JoinPlannedRoutes(p.Sites, p.Routes, closureRead with
                { Routes = EquatableArray.From(closureRead.Routes.Where(r => r.Projection.Length == 0)) });
            var (thermal, _, prerequisites) = await PlannedShelterReadsAsync(snapshot, p.Colony, p.Colony.Facts.PlannedShelters, sites, ct, predictions).ConfigureAwait(false);
            result.Add(new(p.Candidate, p.Colony with { Facts = p.Colony.Facts with
            { PlannedSites = sites, PlannedRoomPredictions = predictions, PlannedThermal = thermal, RoomPrerequisites = prerequisites } }));
        }
        return result;
    }

    internal static string CandidateKey(string candidate, string key) => candidate + "/" + key;

    internal static EquatableArray<PlannedRoomPrediction> CandidatePredictions(string candidate, IEnumerable<PlannedRoomPrediction> predictions)
    {
        var prefix = candidate + "/";
        return EquatableArray.From(predictions.Where(r => r.Room.StartsWith(prefix, StringComparison.Ordinal)).Select(r => r with
        {
            Room = r.Room[prefix.Length..], Routes = EquatableArray.From(r.Routes.Select(route => route with { Projection = route.Projection[prefix.Length..] })),
        }));
    }
}
