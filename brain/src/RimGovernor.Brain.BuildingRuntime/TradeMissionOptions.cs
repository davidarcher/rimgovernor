namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    internal async Task<IReadOnlyList<Pol.TradeAcquisitionOption>> SettlementAcquisitionOptionsAsync(GenerationSnapshot world, string owner, ColonyReading p, IReadOnlyList<Pol.SupplyDemandResult> demands, Bridge.DefinitionCatalog catalog, CancellationToken ct)
    {
        lock (Census.Mu)
        {
            if (Census.Latest is { } latest
                && latest.Reading.Reading.Identity.SameContext(p.Identity)
                && latest.Reading.Reading.Identity.Tick == p.Identity.Tick)
            {
                var f = latest.Reading.Reading.Facts;
                p = p with
                {
                    Facts = p.Facts with
                    {
                        QuestCalm = f.QuestCalm,
                        QuestRoster = f.QuestRoster,
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
        RimGovernor.Contracts.Wire.Observations.WorldProgressionSnapshot progress;
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
        var silver = p.Facts.Silver();
        var budget = Pol.TradePolicyRules.TradeMissionBudget(silver, p.Facts.Census.ColonistCount);
        if (budget <= 0)
        {
            return [];
        }
        var output = new List<Pol.TradeAcquisitionOption>();
        foreach (var settlement in destinations.Settlements)
        {
            var supplied = new HashSet<Pol.ResourceKey>();
            foreach (var d in demands)
            {
                if (TraderKindCanSupply(catalog, settlement.TraderKind, d.Demand.Good))
                {
                    supplied.Add(d.Demand.Good);
                }
            }
            var definitions = supplied.Contains(Pol.SupplyPlanRules.NutritionKey)
                ? TraderSupplyDefinitions(catalog, settlement.TraderKind)
                : [];
            var (cargo, compatible) = Pol.TradePolicyRules.TradeMissionCargo(demands, supplied, definitions, Policy.FoodTargetDays);
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

    /// <summary>
    /// Whether a trader kind supplies a good, remembered per catalog (TradeAcquisitionMemory.SupplyCompatible). A
    /// generator form the catalog cannot classify supplies nothing a mission may be sent for.
    /// </summary>
    internal bool TraderKindCanSupply(Bridge.DefinitionCatalog catalog, string kind, Pol.ResourceKey good)
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
        var supplies = catalog.TraderKindCanSupply(kind, good) == Pol.TradeCompatibility.Trades;
        lock (m.Mu)
        {
            m.ForCatalog(catalog);
            m.SupplyCompatible[(kind, good)] = supplies;
        }
        return supplies;
    }

    /// <summary>
    /// Every prepared food a trader kind's stock generators supply, with its shown nutrition, by name, remembered
    /// per catalog: the settlement search once rebuilt this list, thousands of stat reads long, for every
    /// settlement at every review.
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
        var definitions = new List<Pol.MissionSupplyDefinition>();
        foreach (var name in catalog.ThingDefs.Keys.Order(StringComparer.Ordinal))
        {
            Pol.TradeFoodGood? food;
            try
            {
                food = catalog.TradeFood(name);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                food = null;
            }
            if (food is not { Prepared: true }
                || !RoundsTradePlanner.MissionCatalogStat(catalog, name, Bridge.CatalogStats.Nutrition, out var nutrition)
                || nutrition <= 0
                || !TraderKindCanSupply(catalog, kind, new Pol.ResourceKey { Def = new Pol.Resource(name) }))
            {
                continue;
            }
            definitions.Add(new Pol.MissionSupplyDefinition { Definition = name, Nutrition = nutrition });
        }
        lock (m.Mu)
        {
            m.ForCatalog(catalog);
            m.SupplyDefinitions[kind] = definitions;
        }
        return definitions;
    }
}
