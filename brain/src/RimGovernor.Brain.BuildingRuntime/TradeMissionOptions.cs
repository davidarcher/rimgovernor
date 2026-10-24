namespace RimGovernor.Brain.BuildingRuntime;

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
        if (Native is not FacadeNativeClient source)
        {
            return [];
        }
        try
        {
            // One mission at a time: none is offered while the economy/trade-mission Need has one under way.
            if (Player.Journal.LoadTradeMissions(Player.PlayerWorld(world), ct).Missions.Count != 0)
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
            progress = await source.ReadWorldProgressionAsync(id, ct).ConfigureAwait(false);
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
        O.WorldSnapshot destinations;
        try
        {
            destinations = await source.ReadWorldAsync(id, home, double.MaxValue, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
        if (!Pol.TradePolicyRules.TradeMissionBudget(p.Facts.Silver(), p.Facts.Colonists).TryGetValue(out var budget) || budget <= 0)
        {
            return [];
        }
        var output = new List<Pol.TradeAcquisitionOption>();
        foreach (var settlement in destinations.Settlements)
        {
            var compatibleFacts = new Dictionary<Pol.ResourceKey, Fact<bool>>();
            foreach (var d in demands)
            {
                compatibleFacts[d.Demand.Good] = TraderKindCanSupply(catalog, settlement.TraderKind, d.Demand.Good);
            }
            var definitions = compatibleFacts.ContainsKey(Pol.SupplyPlanRules.NutritionKey)
                ? TraderSupplyDefinitions(catalog, settlement.TraderKind)
                : [];
            var (cargo, compatible) = Pol.TradePolicyRules.TradeMissionCargo(demands, compatibleFacts, definitions, Policy.FoodTargetDays);
            if (cargo.Count == 0)
            {
                continue;
            }
            TradeMission mission;
            Pol.TradeAcquisitionOption option;
            try
            {
                (mission, option) = await RoundsTradePlanner.PrepareTradeMissionAsync(source, world, home, settlement, p.Facts, Seasonal(p.Facts), budget, cargo, null, ct).ConfigureAwait(false);
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

    /// <summary>Whether a trader kind supplies a good, remembered per catalog (TradeAcquisitionMemory.SupplyCompatible).</summary>
    internal Fact<bool> TraderKindCanSupply(Bridge.DefinitionCatalog catalog, string kind, Pol.ResourceKey good)
    {
        var m = TradeAcquisition;
        lock (m.Mu)
        {
            m.ForCatalog(catalog);
            if (m.SupplyCompatible.TryGetValue((kind, good), out var held))
            {
                return held;
            }
        }
        var fact = catalog.TraderKindCanSupply(kind, good);
        lock (m.Mu)
        {
            m.ForCatalog(catalog);
            m.SupplyCompatible[(kind, good)] = fact;
        }
        return fact;
    }

    /// <summary>
    /// Every ThingDef as a mission supply definition for a trader kind (its food class, nutrition and whether the
    /// kind's stock generators supply it), by name, remembered per catalog: the settlement search once rebuilt this
    /// list, thousands of stat reads long, for every settlement at every review.
    /// </summary>
    internal IReadOnlyList<Pol.MissionSupplyDefinition> TraderSupplyDefinitions(Bridge.DefinitionCatalog catalog, string kind)
    {
        var m = TradeAcquisition;
        lock (m.Mu)
        {
            m.ForCatalog(catalog);
            if (m.SupplyDefinitions.TryGetValue(kind, out var held))
            {
                return held;
            }
        }
        var definitions = new List<Pol.MissionSupplyDefinition>(catalog.ThingDefs.Count);
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
                Prepared = RoundsTradePlanner.MissionOptional(food?.Prepared ?? false, known),
                Nutrition = RoundsTradePlanner.MissionCatalogStat(catalog, name, Bridge.CatalogStats.Nutrition),
                CanSupply = TraderKindCanSupply(catalog, kind, new Pol.ResourceKey { Def = new Pol.Resource(name) }),
            });
        }
        lock (m.Mu)
        {
            m.ForCatalog(catalog);
            m.SupplyDefinitions[kind] = definitions;
        }
        return definitions;
    }
}
