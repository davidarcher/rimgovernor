namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    internal static Fact<IReadOnlyList<WorldSite>> FrameWorldSites(WorldProgressionRead? read)
    {
        if (read is null)
        {
            return Unknown<IReadOnlyList<WorldSite>>();
        }
        var rows = new List<WorldSite>(read.Sites.Count);
        foreach (var site in read.Sites)
        {
            var row = new WorldSite
            {
                Id = site.Id,
                Def = site.DefName,
                Label = site.Label,
                Tile = Optional(site.HasTile, site.Tile),
                Layer = Optional(site.HasLayerId, site.LayerId),
                State = site.State,
                DistanceTiles = Optional(site.HasDistanceTiles, site.DistanceTiles),
                Threat = Optional(site.HasThreat, site.Threat),
                ThreatPoints = Optional(site.HasThreatPoints, site.ThreatPoints),
                Reachable = Optional(site.HasReachable, site.Reachable),
                TravelTicks = Optional(site.HasTravelTicks, (long)site.TravelTicks),
            };
            if (site.HasMapId)
            {
                row = row with { Map = Known(new MapId(site.MapId)) };
            }
            var questIds = new List<QuestId>(site.QuestIds.Count);
            foreach (var id in site.QuestIds)
            {
                questIds.Add(new QuestId(id));
            }
            var routePawns = new List<RimGovernor.Brain.Domain.PawnId>(site.RoutePawnIds.Count);
            foreach (var id in site.RoutePawnIds)
            {
                routePawns.Add(new RimGovernor.Brain.Domain.PawnId(id));
            }
            row = row with { QuestIds = questIds, RoutePawnIds = routePawns };
            if (site.Security is { } s)
            {
                row = row with
                {
                    Security = Known(new SiteSecurity
                    {
                        Known = Optional(s.HasKnown, s.Known),
                        InitialPoints = Optional(s.HasInitialPoints, s.InitialPoints),
                        PendingRaidPoints = Optional(s.HasPendingRaidPoints, s.PendingRaidPoints),
                        ActiveThreat = Optional(s.HasActiveThreat, s.ActiveThreat),
                        DormantThreat = Optional(s.HasDormantThreat, s.DormantThreat),
                        TrapCount = Optional(s.HasTrapCount, (int)s.TrapCount),
                        DetectionActive = Optional(s.HasDetectionActive, s.DetectionActive),
                        DetectionTicksLeft = Optional(s.HasDetectionTicksLeft, (long)s.DetectionTicksLeft),
                        RaidsSent = Optional(s.HasRaidsSent, (int)s.RaidsSent),
                    }),
                };
            }
            if (site.PeaceTalks is { } p)
            {
                row = row with
                {
                    PeaceTalks = Known(new PeaceTalksRisk
                    {
                        WorstGoodwillLoss = Optional(p.HasWorstGoodwillLoss, p.WorstGoodwillLoss),
                        BestGoodwillGain = Optional(p.HasBestGoodwillGain, p.BestGoodwillGain),
                        IdeologyActive = Optional(p.HasIdeologyActive, p.IdeologyActive),
                    }),
                };
            }
            if (site.Extraction is { } e)
            {
                var crew = new List<RimGovernor.Brain.Domain.PawnId>(e.CrewIds.Count);
                foreach (var id in e.CrewIds)
                {
                    crew.Add(new RimGovernor.Brain.Domain.PawnId(id));
                }
                var cargo = new List<SiteCargo>(e.Cargo.Count);
                foreach (var c in e.Cargo)
                {
                    cargo.Add(new SiteCargo
                    {
                        Id = c.Id,
                        Def = c.Def,
                        Count = Optional(c.HasCount, (long)c.Count),
                        UnitMass = Optional(c.HasUnitMass, c.UnitMass),
                        MarketValue = Optional(c.HasMarketValue, c.MarketValue),
                        Nutrition = Optional(c.HasNutrition, c.Nutrition),
                        Held = Optional(c.HasHeld, c.Held),
                    });
                }
                var routes = new List<SiteHomeRoute>(e.HomeRoutes.Count);
                foreach (var r in e.HomeRoutes)
                {
                    routes.Add(new SiteHomeRoute
                    {
                        Map = new MapId(r.MapId),
                        Tile = (int)r.Tile,
                        Reachable = Optional(r.HasReachable, r.Reachable),
                        TravelTicks = Optional(r.HasTravelTicks, (long)r.TravelTicks),
                    });
                }
                row = row with
                {
                    Extraction = Known(new SiteExtraction
                    {
                        CanReform = Optional(e.HasCanReform, e.CanReform),
                        CarryCapacity = Optional(e.HasCarryCapacity, e.CarryCapacity),
                        CarriedMass = Optional(e.HasCarriedMass, e.CarriedMass),
                        InventoryFoodDays = Optional(e.HasInventoryFoodDays, e.InventoryFoodDays),
                        CrewNutritionPerDay = Optional(e.HasCrewNutritionPerDay, e.CrewNutritionPerDay),
                        Crew = crew,
                        Cargo = cargo,
                        HomeRoutes = routes,
                        ExitCells = CellsOf(e.ExitCells),
                    }),
                };
            }
            var mining = new List<SiteMiningTarget>(site.MiningTargets.Count);
            foreach (var m in site.MiningTargets)
            {
                mining.Add(new SiteMiningTarget
                {
                    Id = m.Id,
                    Def = m.Def,
                    Map = new MapId(m.MapId),
                    Cell = CellOf(m.Cell),
                    Designated = Optional(m.HasDesignated, m.Designated),
                });
            }
            rows.Add(row with { MiningTargets = mining });
        }
        return Known<IReadOnlyList<WorldSite>>(rows);
    }

    internal static Fact<QuestSurveyScanner> QuestSurveyScannerOf(O.QuestSurveyScanner? scanner) =>
        scanner is null
            ? Unknown<QuestSurveyScanner>()
            : Known(new QuestSurveyScanner
            {
                SiteId = scanner.SiteId,
                ScannerId = scanner.ScannerId,
                DurationTicks = Optional(scanner.HasDurationTicks, (long)scanner.DurationTicks),
                EndTick = Optional(scanner.HasEndTick, (long)scanner.EndTick),
                RaidTick = Optional(scanner.HasRaidTick, (long)scanner.RaidTick),
                Alive = Optional(scanner.HasAlive, scanner.Alive),
                Complete = Optional(scanner.HasComplete, scanner.Complete),
            });

    internal static Fact<QuestGravEngine> QuestGravEngineOf(O.QuestGravEngine? engine)
    {
        if (engine is null)
        {
            return Unknown<QuestGravEngine>();
        }
        var eligible = new List<RimGovernor.Brain.Domain.PawnId>(engine.EligiblePawnIds.Count);
        foreach (var id in engine.EligiblePawnIds)
        {
            eligible.Add(new RimGovernor.Brain.Domain.PawnId(id));
        }
        var inspecting = new List<RimGovernor.Brain.Domain.PawnId>(engine.InspectingPawnIds.Count);
        foreach (var id in engine.InspectingPawnIds)
        {
            inspecting.Add(new RimGovernor.Brain.Domain.PawnId(id));
        }
        return Known(new QuestGravEngine
        {
            Id = engine.EngineId,
            Map = new MapId(engine.MapId),
            Spawned = Optional(engine.HasSpawned, engine.Spawned),
            Inspected = Optional(engine.HasInspected, engine.Inspected),
            Cell = engine.Cell is null ? default : CellOf(engine.Cell),
            EligiblePawns = eligible,
            InspectingPawnIds = inspecting,
        });
    }
}
