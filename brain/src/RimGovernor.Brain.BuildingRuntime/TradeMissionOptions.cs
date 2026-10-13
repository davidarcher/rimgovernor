namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>What <see cref="Rounder.SettlementAcquisitionOptions"/> needs of the native source beyond the mission pack source.</summary>
internal interface ISettlementOptionsSource : ITradeMissionPackSource
{
    Task<(WorldProgressionRead Read, BridgeResult Result)> ReadWorldProgressionAsync(C.Identity identity, bool full, CancellationToken ct);

    Task<(WorldRead Read, BridgeResult Result)> ReadWorldAsync(C.Identity identity, int home, double radius, CancellationToken ct);
}

public sealed partial class Rounder
{
    internal async Task<IReadOnlyList<Pol.TradeAcquisitionOption>> SettlementAcquisitionOptionsAsync(GenerationSnapshot world, string owner, Observation.ColonyProjection p, IReadOnlyList<Pol.SupplyDemandResult> demands, Bridge.DefinitionCatalog catalog, CancellationToken ct)
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
            var projects = Player.Journal.OpenStandards(Concerns.TradeMission, ct);
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
            (progress, _) = await source.ReadWorldProgressionAsync(id, false, ct).ConfigureAwait(false);
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
            (destinations, _) = await source.ReadWorldAsync(id, home, double.MaxValue, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
        if (!Pol.PolicyFunctions.TradeMissionBudget(p.Facts.Silver(), p.Facts.Colonists).TryGetValue(out var budget) || budget <= 0)
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
            if (compatibleFacts.ContainsKey(Pol.PolicyFunctions.NutritionKey))
            {
                foreach (var name in catalog.ThingDefs.Keys.Order(StringComparer.Ordinal))
                {
                    Pol.TradeFoodGood? food = null;
                    var known = false;
                    try
                    {
                        food = catalog.TradeFood(name);
                        known = food is not null;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        known = false;
                    }
                    definitions.Add(new Pol.MissionSupplyDefinition
                    {
                        Definition = name,
                        Prepared = MissionOptional(food?.Prepared ?? false, known),
                        Nutrition = MissionCatalogStat(catalog, name, Bridge.StatNames.Nutrition),
                        CanSupply = catalog.TraderKindCanSupply(settlement.TraderKind, new Pol.ResourceKey { Def = new Pol.Resource(name) }),
                    });
                }
            }
            var (cargo, compatible) = Pol.PolicyFunctions.TradeMissionCargo(demands, compatibleFacts, definitions, Policy.FoodTargetDays);
            if (cargo.Count == 0)
            {
                continue;
            }
            TradeMission mission;
            Pol.TradeAcquisitionOption option;
            try
            {
                (mission, option) = await TradeMissions.PrepareTradeMissionAsync(source, world, home, settlement, p.Facts, Seasonal(p.Facts), budget, cargo, null, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }
            option = option with { Compatible = [.. compatible], ID = owner + ":" + option.ID };
            lock (TradeAcquisition.Mu)
            {
                TradeAcquisition.Missions[option.ID] = mission;
            }
            output.Add(option);
        }
        return output;
    }
}
