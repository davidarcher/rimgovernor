using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

/// <summary>
/// Native source potential, read whole every colony read. It does not select work.
/// </summary>
public sealed record FoodChannels
{
    /// <summary>Null without Odyssey.</summary>
    public FishableWater? FishableWater { get; init; }

    /// <summary>The producing milk and egg yields; an inactive or stalled comp contributes nothing and has no row. Eggs need no handler.</summary>
    public IReadOnlyList<AnimalProduct> Products { get; init; } = [];

    public IReadOnlyList<O.PasteDispenser> PasteDispenser { get; init; } = [];

    public EquatableArray<PenGrazing> Grazing { get; init; }

    public PastureTable Pasture { get; init; } = new(new Dictionary<string, double>());

    public IReadOnlyList<SlaughterFoodAnimal> Slaughter { get; init; } = [];
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

public static class FoodChannelsReads
{
    /// <summary>Called only after the colony boundary validator has checked the section.</summary>
    internal static FoodChannels ColonyFoodChannels(O.FoodChannelsFacts v, Pawns pawns, AnimalRaceCatalog races)
    {
        var grazing = new List<PenGrazing>();
        foreach (var row in v.Grazing)
        {
            grazing.Add(new PenGrazing { DemandPerDay = row.DemandPerDay, PasturePerDay = row.PasturePerDay, StoredNutrition = row.StoredNutrition });
        }
        var pasture = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in v.PastureTerrain)
        {
            pasture[row.TerrainDef] = row.WorstQuadrumPerDay;
        }
        var slaughter = new List<SlaughterFoodAnimal>();
        foreach (var row in v.Slaughter)
        {
            slaughter.Add(SlaughterFood(row, pawns, races));
        }
        var products = new List<AnimalProduct>();
        foreach (var row in v.Gatherable)
        {
            if (row.Yield is O.AnimalYield.Producing p)
            {
                products.Add(new AnimalProduct { Pawn = row.PawnId, Race = row.Race, Reachable = row.HandlerReachable, NutritionPerDay = p.NutritionPerDay, WorkPerDay = p.WorkPerDay, LeadDays = p.LeadDays });
            }
        }
        foreach (var row in v.EggLayer)
        {
            if (row.Yield is O.AnimalYield.Producing p)
            {
                products.Add(new AnimalProduct { Pawn = row.PawnId, Race = row.Race, Reachable = true, NutritionPerDay = p.NutritionPerDay, WorkPerDay = p.WorkPerDay, LeadDays = p.LeadDays });
            }
        }
        return new FoodChannels
        {
            Grazing = [.. grazing],
            Pasture = new PastureTable(pasture),
            Slaughter = slaughter,
            Products = products,
            PasteDispenser = [.. v.PasteDispenser],
            FishableWater = v.FishableWater is { } water ? ColonyFishableWater(water) : null,
        };
    }

    /// <summary>
    /// The meat, feed and reproduction numbers of one animal the colony would eat. Native sends the animal's live
    /// MeatAmount only when the colony eats the meat; the race row gives the meat's nutrition, the life stage's
    /// hunger scales the adult feed (for a plant eater), and the egg interval or the gestation is the
    /// reproduction period.
    /// </summary>
    private static SlaughterFoodAnimal SlaughterFood(O.FoodSlaughterAnimal row, Pawns pawns, AnimalRaceCatalog races)
    {
        var output = new SlaughterFoodAnimal { ID = new PawnId(row.PawnId), Race = new Resource(row.Race) };
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
        if (!race.AdultFeedPerDay.TryGetValue(out var adult) || !pawns.TryGetValue(id, out var row) || row?.AnimalState is not O.PawnAnimalState.Animal { State: var state })
        {
            return false;
        }
        var stages = race.LifeStages;
        var index = state.LifeStageIndex;
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
        var researched = ColonyDecodeReads.Optional(water.FishingResearched is not null, water.FishingResearched.GetValueOrDefault());
        var w = new FishableWater
        {
            FishingResearched = researched,
            ResearchLeadDays = FishingFactsRules.FishingResearchLeadDays(new FishingResearchInputs
            {
                Researched = researched,
                BaseCost = ColonyDecodeReads.Optional(water.ResearchBaseCost is not null, water.ResearchBaseCost.GetValueOrDefault()),
                Progress = ColonyDecodeReads.Optional(water.ResearchProgress is not null, water.ResearchProgress.GetValueOrDefault()),
                CostFactor = ColonyDecodeReads.Optional(water.ResearchCostFactor is not null, water.ResearchCostFactor.GetValueOrDefault()),
                ResearcherSpeeds = [.. water.ResearcherSpeeds],
                PointsPerWorkTick = ColonyDecodeReads.Optional(water.ResearchPointsPerWorkTick is not null, water.ResearchPointsPerWorkTick.GetValueOrDefault()),
                Difficulty = ColonyDecodeReads.Optional(water.ResearchDifficultySpeedFactor is not null, water.ResearchDifficultySpeedFactor.GetValueOrDefault()),
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
                Root = row.Root,
                Population = ColonyDecodeReads.Optional(row.Population is not null, row.Population.GetValueOrDefault()),
                MaxPopulation = ColonyDecodeReads.Optional(row.MaxPopulation is not null, row.MaxPopulation.GetValueOrDefault()),
                Zoned = ColonyDecodeReads.Optional(row.Zoned is not null, row.Zoned.GetValueOrDefault()),
                CellCount = ColonyDecodeReads.Optional(row.CellCount is not null, row.CellCount.GetValueOrDefault()),
                Frozen = ColonyDecodeReads.Optional(row.Frozen is not null, row.Frozen.GetValueOrDefault()),
                NutritionPerFish = ColonyDecodeReads.Optional(row.NutritionPerFish is not null, row.NutritionPerFish.GetValueOrDefault()),
                FishPerBatch = ColonyDecodeReads.Optional(row.FishPerBatch is not null, row.FishPerBatch.GetValueOrDefault()),
                WorkTicksPerBatch = ColonyDecodeReads.Optional(row.WorkTicksPerBatch is not null, row.WorkTicksPerBatch.GetValueOrDefault()),
                DistanceSquared = ColonyDecodeReads.Optional(row.NearestDistanceSquared is not null, row.NearestDistanceSquared.GetValueOrDefault()),
                ConcurrentFishers = Known((uint)fishers.Count),
            };
            var cells = new List<FishingCell>(row.Cells.Count);
            foreach (var c in row.Cells)
            {
                cells.Add(new FishingCell { Cell = c.Cell, Reachable = c.Reachable, DistanceSquared = c.DistanceSquared });
            }
            var proposed = FishingFactsRules.FishingFootprint(cells, fishers.Count);
            region = region with
            {
                ProposedCells = proposed,
                Reachable = FishingFactsRules.FishingReachable(ColonyDecodeReads.Optional(row.Reachable is not null, row.Reachable.GetValueOrDefault()), region.Zoned, proposed.Count, fishers.Count),
            };
            var zones = new List<FishingZoneState>(row.Zones.Count);
            foreach (var z in row.Zones)
            {
                zones.Add(new FishingZoneState(z.Allowed, z.DoForever, z.HasFishableCells));
            }
            region = region with
            {
                Delivering = Known(FishingFactsRules.FishingDelivering(zones)),
                PawnFishWorkCapacity = FishingFactsRules.FishingWorkCapacity(fishers, ColonyDecodeReads.Optional(row.YieldCurveValue is not null, row.YieldCurveValue.GetValueOrDefault()), region.NutritionPerFish, ColonyDecodeReads.Optional(water.BaseFishingDurationTicks is not null, water.BaseFishingDurationTicks.GetValueOrDefault())),
            };
            regions.Add(region);
        }
        return w with { Regions = regions };
    }
}
