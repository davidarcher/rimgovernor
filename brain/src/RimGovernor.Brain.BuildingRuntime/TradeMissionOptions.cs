namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What <see cref="Rounder.SettlementAcquisitionOptions"/> needs of the native source beyond the mission pack source.</summary>
internal interface ISettlementOptionsSource : ITradeMissionPackSource
{
    (WorldProgressionRead Read, BridgeResult Result) ReadWorldProgression(CancellationToken ct, C.Identity identity, bool full);

    (WorldRead Read, BridgeResult Result) ReadWorld(CancellationToken ct, C.Identity identity, int home, double radius);
}

public sealed partial class Rounder
{
    internal IReadOnlyList<Pol.TradeAcquisitionOption> SettlementAcquisitionOptions(
        CancellationToken ct, GenerationSnapshot world, string owner, Observation.ColonyProjection p, IReadOnlyList<Pol.SupplyDemandResult> demands, Bridge.DefinitionCatalog catalog)
    {
        lock (Census.Mu)
        {
            if (Census.Latest is { } latest
                && latest.Reading.Projection.Identity.SameContext(p.Identity)
                && latest.Reading.Projection.Identity.Tick == p.Identity.Tick)
            {
                var f = latest.Reading.Projection.Facts;
                p = p with
                {
                    Facts = p.Facts with
                    {
                        QuestColonyCalm = f.QuestColonyCalm,
                        QuestSparePawns = f.QuestSparePawns,
                        QuestHomeFloor = f.QuestHomeFloor,
                        QuestDepartureWork = f.QuestDepartureWork,
                        WorkRoster = f.WorkRoster,
                    },
                };
            }
        }
        if (Native is not ISettlementOptionsSource source)
        {
            return [];
        }
        try
        {
            var projects = Player.Journal.OpenStandards(ct, Concerns.TradeMission);
            if (projects.Count != 0)
            {
                return [];
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
        var id = Boundary.Identity(world);
        WorldProgressionRead progress;
        try
        {
            (progress, _) = source.ReadWorldProgression(ct, id, false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
        var home = -1;
        foreach (var row in progress.Maps)
        {
            if (row.Home && new MapId(row.Id) == world.Map)
            {
                home = row.Tile;
            }
        }
        if (home < 0)
        {
            return [];
        }
        // Read the complete native settlement census; routes, not radius, determine which exact pack can safely
        // make the round trip.
        WorldRead destinations;
        try
        {
            (destinations, _) = source.ReadWorld(ct, id, home, double.MaxValue);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
        if (!Pol.TradeMission.TradeMissionBudget(p.Facts.Silver(), p.Facts.Colonists).TryGetValue(out var budget) || budget <= 0)
        {
            return [];
        }
        var output = new List<Pol.TradeAcquisitionOption>();
        foreach (var settlement in destinations.Settlements)
        {
            var compatibleFacts = new Dictionary<Pol.ResourceKey, Fact<bool>>();
            foreach (var d in demands)
            {
                compatibleFacts[d.Demand.Good] = catalog.TraderKindCanSupply(settlement.TraderKind, d.Demand.Good);
            }
            var definitions = new List<Pol.MissionSupplyDefinition>();
            if (compatibleFacts.ContainsKey(Pol.ResourceKey.NutritionKey))
            {
                foreach (var name in catalog.ThingDefs.Keys.Order(StringComparer.Ordinal))
                {
                    Bridge.TradeFood food = default;
                    var known = false;
                    try
                    {
                        (food, known) = catalog.TradeFood(name);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        known = false;
                    }
                    definitions.Add(new Pol.MissionSupplyDefinition
                    {
                        Definition = name,
                        Prepared = MissionOptional(food.Prepared, known),
                        Nutrition = MissionCatalogStat(catalog, name, Bridge.Stat.Nutrition),
                        CanSupply = catalog.TraderKindCanSupply(settlement.TraderKind, new Pol.ResourceKey { Def = new Pol.Resource(name) }),
                    });
                }
            }
            var (cargo, compatible) = Pol.TradeMission.TradeMissionCargo(demands, compatibleFacts, definitions, Pol.FoodTargetDays);
            if (cargo.Count == 0)
            {
                continue;
            }
            TradeMission mission;
            Pol.TradeAcquisitionOption option;
            try
            {
                (mission, option) = TradeMissions.PrepareTradeMission(ct, source, world, home, settlement, p.Facts, Seasonal(p.Facts), budget, cargo, null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }
            option = option with { Compatible = compatible, Id = owner + ":" + option.Id };
            lock (TradeAcquisition.Mu)
            {
                TradeAcquisition.Missions[option.Id] = mission;
            }
            output.Add(option);
        }
        return output;
    }
}
