namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>Reads complete destination and exact packing facts before the portfolio comparison, independently of demand.</summary>
    internal async Task<EquatableArray<Pol.ForecastSettlementMission>> SettlementForecastOptionsAsync(GenerationSnapshot world, ColonyReading reading, Bridge.DefinitionCatalog? catalog, CancellationToken ct)
    {
        if (Native is not GameClient source) return [];
        if (catalog is null) throw new Pol.RequiredFactException("definition_catalog");
        if (Player.Journal.LoadTradeMissions(Player.PlayerWorld(world), ct).Missions.Count != 0) return [];
        var id = Boundary.Identity(world);
        var progress = await source.ReadWorldProgressionAsync(id, ct).ConfigureAwait(false);
        var home = progress.Maps.SingleOrDefault(m => m.Home && m.Id == world.Map.Value);
        if (home is null) return [];
        var budget = Pol.TradePolicyRules.TradeMissionBudget(reading.Facts.Silver(), reading.Facts.Census.ColonistCount);
        if (budget <= 0) return [];
        var destinations = await source.ReadWorldAsync(id, home.Tile, TradeExportPlanner.AllSettlementsRadius, ct).ConfigureAwait(false);
        var result = new List<Pol.ForecastSettlementMission>();
        foreach (var settlement in destinations.Settlements.Where(s => s.CanTrade && s.Relation == RimGovernor.Brain.GameFacts.Observations.SettlementRelation.Neutral).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            try
            {
                var (departure, _, pack) = await TradePlanner.PrepareTradeMissionOptionAsync(source, world, home.Tile, settlement,
                    reading.Facts, Seasonal(reading.Facts), budget, null, ct).ConfigureAwait(false);
                var option = Pol.ForecastSettlementMissions.Option(catalog.TraderStock(), settlement, pack, departure.Crew.Select(p => p.Value).ToArray(), budget);
                if (option is not null) result.Add(option);
            }
            catch (DomainException e) when (e is not Pol.RequiredFactException)
            {
                // A concretely refused destination/crew/food/packing option is infeasible; native read failures propagate.
                continue;
            }
        }
        return EquatableArray.From(result);
    }
}
