namespace RimGovernor.Brain.Observation;

/// <summary>
/// Native source potential. It does not select work. A known census can contain animals with unknown
/// component values.
/// </summary>
public sealed record FoodChannels
{
    public Fact<FishableWater> FishableWater { get; init; }

    public IReadOnlyList<GatherableAnimal> Gatherable { get; init; } = [];

    public IReadOnlyList<EggLayerAnimal> EggLayer { get; init; } = [];

    public IReadOnlyList<PasteDispenser> PasteDispenser { get; init; } = [];

    public Fact<uint> PollutedCells { get; init; }

    public IReadOnlyList<ForagePlant> Forage { get; init; } = [];

    public IReadOnlyList<PenGrazing> Grazing { get; init; } = [];

    public PastureTable? Pasture { get; init; }

    public IReadOnlyList<SlaughterFoodAnimal> Slaughter { get; init; } = [];

    /// <summary>Keeps native unknown rates unknown. Non-food comps and inactive animals have no nutrition to contribute; eggs require no handler gathering.</summary>
    public IReadOnlyList<AnimalProduct> AnimalProducts()
    {
        var rows = new List<AnimalProduct>();
        foreach (var a in Gatherable)
        {
            rows.Add(new AnimalProduct { Pawn = a.PawnID, Race = a.Race, Active = a.Active, Reachable = a.HandlerReachable, NutritionPerDay = a.NutritionPerDay, WorkPerDay = a.WorkPerDay, LeadDays = a.LeadDays });
        }
        foreach (var a in EggLayer)
        {
            rows.Add(new AnimalProduct { Pawn = a.PawnID, Race = a.Race, Active = a.Active, Reachable = Known(true), NutritionPerDay = a.NutritionPerDay, WorkPerDay = Known(0.0), LeadDays = a.LeadDays });
        }
        return rows;
    }
}

public sealed record FishableWater
{
    public IReadOnlyList<FishableRegion> Regions { get; init; } = [];

    public Fact<bool> FishingResearched { get; init; }

    public Fact<double> ResearchLeadDays { get; init; }
}

public sealed record FishableRegion
{
    public Cell Root { get; init; }

    public Fact<double> Population { get; init; }

    public Fact<double> MaxPopulation { get; init; }

    public Fact<bool> Zoned { get; init; }

    public Fact<bool> Reachable { get; init; }

    public Fact<uint> CellCount { get; init; }

    public Fact<bool> Frozen { get; init; }

    public Fact<bool> Delivering { get; init; }

    public Fact<double> NutritionPerFish { get; init; }

    public Fact<double> FishPerBatch { get; init; }

    public Fact<double> WorkTicksPerBatch { get; init; }

    public IReadOnlyList<Cell> ProposedCells { get; init; } = [];

    public Fact<double> PawnFishWorkCapacity { get; init; }

    public Fact<uint> ConcurrentFishers { get; init; }

    public Fact<double> DistanceSquared { get; init; }
}

public sealed record GatherableAnimal
{
    public string PawnID { get; init; } = "";

    public string Race { get; init; } = "";

    public Fact<double> Fullness { get; init; }

    public Fact<string> Resource { get; init; }

    public Fact<bool> HandlerReachable { get; init; }

    public Fact<double> NutritionPerDay { get; init; }

    public Fact<double> WorkPerDay { get; init; }

    public Fact<double> LeadDays { get; init; }

    public Fact<bool> Active { get; init; }
}

public sealed record EggLayerAnimal
{
    public string PawnID { get; init; } = "";

    public string Race { get; init; } = "";

    public Fact<bool> CanLayNow { get; init; }

    public Fact<double> Progress { get; init; }

    public Fact<double> NutritionPerDay { get; init; }

    public Fact<double> LeadDays { get; init; }

    public Fact<bool> Active { get; init; }

    /// <summary>The hen's next egg is the fertilized def (CompEggLayer fertilizationCount &gt; 0).</summary>
    public Fact<bool> Fertilized { get; init; }
}

public sealed record PasteDispenser
{
    public string BuildingID { get; init; } = "";

    public Fact<bool> Powered { get; init; }

    public Fact<double> HopperNutrition { get; init; }

    public Fact<string> AdjacentRoomID { get; init; }
}

public sealed record ForagePlant
{
    public string DefName { get; init; } = "";

    public IReadOnlyList<int> GrowingTwelfths { get; init; } = [];

    public Fact<bool> GrowingNow { get; init; }
}

public static partial class ObservationPackage
{
    /// <summary>Called only after the colony boundary validator has checked the section.</summary>
    internal static Fact<FoodChannels> ColonyFoodChannels(O.FoodChannelsSection? section, Pawns pawns, AnimalRaceCatalog races)
    {
        var v = section?.Observed;
        if (v is null)
        {
            return Unknown<FoodChannels>();
        }
        var grazing = new List<PenGrazing>();
        foreach (var row in v.Grazing)
        {
            grazing.Add(new PenGrazing
            {
                ID = row.PenId,
                DemandPerDay = Optional(row.HasDemandPerDay, row.DemandPerDay),
                PasturePerDay = Optional(row.HasPasturePerDay, row.PasturePerDay),
                StoredNutrition = Optional(row.HasStoredNutrition, row.StoredNutrition),
            });
        }
        Dictionary<string, double>? pasture = null;
        foreach (var row in v.PastureTerrain)
        {
            pasture ??= new Dictionary<string, double>(StringComparer.Ordinal);
            pasture[row.TerrainDef] = row.WorstQuadrumPerDay;
        }
        var slaughter = new List<SlaughterFoodAnimal>();
        foreach (var row in v.Slaughter)
        {
            slaughter.Add(SlaughterFood(row, pawns, races));
        }
        var gatherable = new List<GatherableAnimal>();
        foreach (var row in v.Gatherable)
        {
            gatherable.Add(new GatherableAnimal
            {
                PawnID = row.PawnId,
                Race = row.Race,
                Fullness = Optional(row.HasFullness, row.Fullness),
                Resource = Optional(row.HasResource, row.Resource),
                HandlerReachable = Optional(row.HasHandlerReachable, row.HandlerReachable),
                NutritionPerDay = Optional(row.HasNutritionPerDay, row.NutritionPerDay),
                WorkPerDay = Optional(row.HasWorkPerDay, row.WorkPerDay),
                LeadDays = Optional(row.HasLeadDays, row.LeadDays),
                Active = Optional(row.HasActive, row.Active),
            });
        }
        var eggs = new List<EggLayerAnimal>();
        foreach (var row in v.EggLayer)
        {
            eggs.Add(new EggLayerAnimal
            {
                PawnID = row.PawnId,
                Race = row.Race,
                CanLayNow = Optional(row.HasCanLayNow, row.CanLayNow),
                Progress = Optional(row.HasProgress, row.Progress),
                NutritionPerDay = Optional(row.HasNutritionPerDay, row.NutritionPerDay),
                LeadDays = Optional(row.HasLeadDays, row.LeadDays),
                Active = Optional(row.HasActive, row.Active),
                Fertilized = Optional(row.HasFertilized, row.Fertilized),
            });
        }
        var paste = new List<PasteDispenser>();
        foreach (var row in v.PasteDispenser)
        {
            paste.Add(new PasteDispenser
            {
                BuildingID = row.BuildingId,
                Powered = Optional(row.HasPowered, row.Powered),
                HopperNutrition = Optional(row.HasHopperNutrition, row.HopperNutrition),
                AdjacentRoomID = Optional(row.HasAdjacentRoomId, row.AdjacentRoomId),
            });
        }
        var forage = new List<ForagePlant>();
        foreach (var row in v.Forage)
        {
            forage.Add(new ForagePlant { DefName = row.DefName, GrowingTwelfths = [.. row.GrowingTwelfths], GrowingNow = Optional(row.HasGrowingNow, row.GrowingNow) });
        }
        var channels = new FoodChannels
        {
            PollutedCells = Optional(v.HasPollutedCells, v.PollutedCells),
            Grazing = grazing,
            Pasture = pasture is null ? null : new PastureTable(pasture),
            Slaughter = slaughter,
            Gatherable = gatherable,
            EggLayer = eggs,
            PasteDispenser = paste,
            Forage = forage,
        };
        if (v.FishableWater is { } water)
        {
            channels = channels with { FishableWater = Known(ColonyFishableWater(water)) };
        }
        return Known(channels);
    }

    /// <summary>
    /// The meat, feed and reproduction numbers of one animal the colony would eat. Native sends only the animal's
    /// live MeatAmount, and only when the colony eats the meat; the race row gives the meat's nutrition, the life
    /// stage's hunger scales the adult feed (for a plant eater), and the egg interval or the gestation is the
    /// reproduction period.
    /// </summary>
    private static SlaughterFoodAnimal SlaughterFood(O.FoodSlaughterAnimal row, Pawns pawns, AnimalRaceCatalog races)
    {
        var output = new SlaughterFoodAnimal { ID = new PawnId(row.PawnId), Race = new Resource(row.Race) };
        if (!row.HasMeatAmount)
        {
            return output;
        }
        if (!races.Race(output.Race, out var race))
        {
            return output;
        }
        if (race.MeatNutritionPerUnit.TryGetValue(out var per))
        {
            output = output with { MeatNutrition = Known(row.MeatAmount * per) };
        }
        if (race.EatsPlant && StageFeedPerDay(race, pawns, row.PawnId, out var feed))
        {
            output = output with { FeedPerDay = Known(feed) };
        }
        var days = race.GestationDays;
        foreach (var product in race.Products)
        {
            if (product.Kind == "eggs" && product.IntervalDays.TryGetValue(out var interval))
            {
                days = interval;
            }
        }
        if (days > 0)
        {
            output = output with { ReproductionDays = Known(days) };
        }
        return output;
    }

    /// <summary>The nutrition per day the animal eats in its current life stage: the adult feed scaled by the stage's hunger factor against the adult stage's.</summary>
    private static bool StageFeedPerDay(AnimalRace race, Pawns pawns, string id, out double feed)
    {
        feed = 0;
        if (!race.AdultFeedPerDay.TryGetValue(out var adult) || !pawns.TryGet(id, out var row) || row?.AnimalState is not { HasLifeStageIndex: true } state)
        {
            return false;
        }
        var stages = race.LifeStages;
        var index = (int)state.LifeStageIndex;
        if (index < 0 || index >= stages.Count || !(stages[^1].HungerRateFactor > 0))
        {
            return false;
        }
        feed = adult * stages[index].HungerRateFactor / stages[^1].HungerRateFactor;
        return true;
    }

    /// <summary>Applies the policy decisions to native's raw fishing facts: research lead, footprint, reachability verdict, delivery and capacity.</summary>
    private static FishableWater ColonyFishableWater(O.FishableWater water)
    {
        var researched = Optional(water.HasFishingResearched, water.FishingResearched);
        var w = new FishableWater
        {
            FishingResearched = researched,
            ResearchLeadDays = FishingResearchLeadDays(new FishingResearchInputs
            {
                Researched = researched,
                BaseCost = Optional(water.HasResearchBaseCost, water.ResearchBaseCost),
                Progress = Optional(water.HasResearchProgress, water.ResearchProgress),
                CostFactor = Optional(water.HasResearchCostFactor, water.ResearchCostFactor),
                ResearcherSpeeds = [.. water.ResearcherSpeeds],
                PointsPerWorkTick = Optional(water.HasResearchPointsPerWorkTick, water.ResearchPointsPerWorkTick),
                Difficulty = Optional(water.HasResearchDifficultySpeedFactor, water.ResearchDifficultySpeedFactor),
            }),
        };
        var fishers = new List<Fisher>(water.Fishers.Count);
        foreach (var f in water.Fishers)
        {
            fishers.Add(new Fisher(f.FishingYield, f.FishingSpeed));
        }
        var regions = new List<FishableRegion>();
        foreach (var row in water.Regions)
        {
            var region = new FishableRegion
            {
                Root = new Cell(row.Root?.X ?? 0, row.Root?.Z ?? 0),
                Population = Optional(row.HasPopulation, row.Population),
                MaxPopulation = Optional(row.HasMaxPopulation, row.MaxPopulation),
                Zoned = Optional(row.HasZoned, row.Zoned),
                CellCount = Optional(row.HasCellCount, row.CellCount),
                Frozen = Optional(row.HasFrozen, row.Frozen),
                NutritionPerFish = Optional(row.HasNutritionPerFish, row.NutritionPerFish),
                FishPerBatch = Optional(row.HasFishPerBatch, row.FishPerBatch),
                WorkTicksPerBatch = Optional(row.HasWorkTicksPerBatch, row.WorkTicksPerBatch),
                DistanceSquared = Optional(row.HasNearestDistanceSquared, row.NearestDistanceSquared),
                ConcurrentFishers = Known((uint)fishers.Count),
            };
            var cells = new List<FishingCell>(row.Cells.Count);
            foreach (var c in row.Cells)
            {
                cells.Add(new FishingCell { Cell = new Cell(c.Cell?.X ?? 0, c.Cell?.Z ?? 0), Reachable = c.Reachable, DistanceSquared = c.DistanceSquared });
            }
            var proposed = FishingFootprint(cells, fishers.Count);
            region = region with
            {
                ProposedCells = proposed,
                Reachable = FishingReachable(Optional(row.HasReachable, row.Reachable), region.Zoned, proposed.Count, fishers.Count),
            };
            var zones = new List<FishingZoneState>(row.Zones.Count);
            foreach (var z in row.Zones)
            {
                zones.Add(new FishingZoneState(z.Allowed, z.DoForever, z.HasFishableCells));
            }
            region = region with
            {
                Delivering = Known(FishingDelivering(zones)),
                PawnFishWorkCapacity = FishingWorkCapacity(fishers, Optional(row.HasYieldCurveValue, row.YieldCurveValue), region.NutritionPerFish, Optional(water.HasBaseFishingDurationTicks, water.BaseFishingDurationTicks)),
            };
            regions.Add(region);
        }
        return w with { Regions = regions };
    }
}
