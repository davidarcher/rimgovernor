namespace RimGovernor.Brain.Observation;

public static class WorldSitesReads
{
    internal static Fact<EquatableArray<WorldSite>> FrameWorldSites(WorldProgressionRead? read)
    {
        if (read is null)
        {
            return Unknown<EquatableArray<WorldSite>>();
        }
        var rows = new List<WorldSite>(read.Sites.Count);
        foreach (var site in read.Sites)
        {
            var row = new WorldSite
            {
                Id = site.Id ?? "",
                Tile = ColonyDecodeReads.Optional(site.Tile is not null, site.Tile.GetValueOrDefault()),
                Layer = ColonyDecodeReads.Optional(site.LayerId is not null, site.LayerId.GetValueOrDefault()),
                State = site.State,
                Threat = ColonyDecodeReads.Optional(site.Threat is not null, site.Threat.GetValueOrDefault()),
                ThreatPoints = ColonyDecodeReads.Optional(site.ThreatPoints is not null, site.ThreatPoints.GetValueOrDefault()),
                Reachable = ColonyDecodeReads.Optional(site.Reachable is not null, site.Reachable.GetValueOrDefault()),
                TravelTicks = ColonyDecodeReads.Optional(site.TravelTicks is not null, (long)site.TravelTicks.GetValueOrDefault()),
            };
            if (site.MapId is not null)
            {
                row = row with { Map = Known(new MapId(site.MapId.GetValueOrDefault())) };
            }
            var questIds = new List<QuestId>(site.QuestIds.Count);
            foreach (var id in site.QuestIds)
            {
                questIds.Add(new QuestId(id));
            }
            var routePawns = new List<PawnId>(site.RoutePawnIds.Count);
            foreach (var id in site.RoutePawnIds)
            {
                routePawns.Add(new PawnId(id));
            }
            row = row with { QuestIds = questIds, RoutePawnIds = routePawns };
            if (site.Security is { } s)
            {
                row = row with
                {
                    Security = Known(new SiteSecurity
                    {
                        Known = ColonyDecodeReads.Optional(s.Known is not null, s.Known.GetValueOrDefault()),
                        InitialPoints = ColonyDecodeReads.Optional(s.InitialPoints is not null, s.InitialPoints.GetValueOrDefault()),
                        PendingRaidPoints = ColonyDecodeReads.Optional(s.PendingRaidPoints is not null, s.PendingRaidPoints.GetValueOrDefault()),
                        ActiveThreat = ColonyDecodeReads.Optional(s.ActiveThreat is not null, s.ActiveThreat.GetValueOrDefault()),
                        DormantThreat = ColonyDecodeReads.Optional(s.DormantThreat is not null, s.DormantThreat.GetValueOrDefault()),
                        TrapCount = ColonyDecodeReads.Optional(s.TrapCount is not null, (int)s.TrapCount.GetValueOrDefault()),
                    }),
                };
            }
            if (site.PeaceTalks is { } p)
            {
                row = row with
                {
                    PeaceTalks = Known(new PeaceTalksRisk
                    {
                        WorstGoodwillLoss = ColonyDecodeReads.Optional(p.WorstGoodwillLoss is not null, p.WorstGoodwillLoss.GetValueOrDefault()),
                        BestGoodwillGain = ColonyDecodeReads.Optional(p.BestGoodwillGain is not null, p.BestGoodwillGain.GetValueOrDefault()),
                        IdeologyActive = ColonyDecodeReads.Optional(p.IdeologyActive is not null, p.IdeologyActive.GetValueOrDefault()),
                    }),
                };
            }
            if (site.Extraction is { } e)
            {
                var crew = new List<PawnId>(e.CrewIds.Count);
                foreach (var id in e.CrewIds)
                {
                    crew.Add(new PawnId(id));
                }
                var cargo = new List<SiteCargo>(e.Cargo.Count);
                foreach (var c in e.Cargo)
                {
                    cargo.Add(new SiteCargo
                    {
                        Id = c.Id ?? "",
                        Def = c.Def ?? "",
                        Count = ColonyDecodeReads.Optional(c.Count is not null, (long)c.Count.GetValueOrDefault()),
                        UnitMass = ColonyDecodeReads.Optional(c.UnitMass is not null, c.UnitMass.GetValueOrDefault()),
                        MarketValue = ColonyDecodeReads.Optional(c.MarketValue is not null, c.MarketValue.GetValueOrDefault()),
                        Nutrition = ColonyDecodeReads.Optional(c.Nutrition is not null, c.Nutrition.GetValueOrDefault()),
                        Held = ColonyDecodeReads.Optional(c.Held is not null, c.Held.GetValueOrDefault()),
                    });
                }
                var routes = new List<SiteHomeRoute>(e.HomeRoutes.Count);
                foreach (var r in e.HomeRoutes)
                {
                    routes.Add(new SiteHomeRoute
                    {
                        Map = new MapId(r.MapId.GetValueOrDefault()),
                        Tile = (int)r.Tile.GetValueOrDefault(),
                        Reachable = ColonyDecodeReads.Optional(r.Reachable is not null, r.Reachable.GetValueOrDefault()),
                        TravelTicks = ColonyDecodeReads.Optional(r.TravelTicks is not null, (long)r.TravelTicks.GetValueOrDefault()),
                    });
                }
                row = row with
                {
                    Extraction = Known(new SiteExtraction
                    {
                        CanReform = ColonyDecodeReads.Optional(e.CanReform is not null, e.CanReform.GetValueOrDefault()),
                        CarryCapacity = ColonyDecodeReads.Optional(e.CarryCapacity is not null, e.CarryCapacity.GetValueOrDefault()),
                        CarriedMass = ColonyDecodeReads.Optional(e.CarriedMass is not null, e.CarriedMass.GetValueOrDefault()),
                        InventoryFoodDays = ColonyDecodeReads.Optional(e.InventoryFoodDays is not null, e.InventoryFoodDays.GetValueOrDefault()),
                        CrewNutritionPerDay = ColonyDecodeReads.Optional(e.CrewNutritionPerDay is not null, e.CrewNutritionPerDay.GetValueOrDefault()),
                        Crew = crew,
                        Cargo = cargo,
                        HomeRoutes = routes,
                        ExitCells = ColonyDecodeReads.CellsOf(e.ExitCells),
                    }),
                };
            }
            var mining = new List<SiteMiningTarget>(site.MiningTargets.Count);
            foreach (var m in site.MiningTargets)
            {
                mining.Add(new SiteMiningTarget
                {
                    Id = m.Id ?? "",
                    Def = m.Def ?? "",
                    Map = new MapId(m.MapId.GetValueOrDefault()),
                    Cell = ColonyDecodeReads.CellOf(m.Cell),
                    Designated = ColonyDecodeReads.Optional(m.Designated is not null, m.Designated.GetValueOrDefault()),
                });
            }
            rows.Add(row with { MiningTargets = mining });
        }
        return Known(EquatableArray.From(rows));
    }
}
