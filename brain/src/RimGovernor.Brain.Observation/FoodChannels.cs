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

    public FishingResearch Research { get; init; } = new FishingResearch.NoProgress();
}

/// <summary>A water body whose fish the colony eats, with the policy verdicts: reachability, delivery, footprint and crew.</summary>
public sealed record FishableRegion
{
    public Cell Root { get; init; }

    public double Population { get; init; }

    public double MaxPopulation { get; init; }

    public bool Zoned { get; init; }

    /// <summary>Some fisher reaches the water, and an unzoned body also offers a footprint of one cell per fisher.</summary>
    public bool Reachable { get; init; }

    public bool Frozen { get; init; }

    public bool Delivering { get; init; }

    public double NutritionPerFish { get; init; }

    public IReadOnlyList<Cell> ProposedCells { get; init; } = [];

    public FishingCrew Crew { get; init; } = new FishingCrew.NoFisher();

    public double DistanceSquared { get; init; }
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
            if (product.Kind == "eggs")
            {
                days = product.IntervalDays;
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

    /// <summary>Applies the policy decisions to native's raw fishing facts: research state, footprint, reachability verdict, delivery and crew.</summary>
    private static FishableWater ColonyFishableWater(O.FishableWater water)
    {
        var research = FishingFactsRules.FishingResearchState(new FishingResearchInputs
        {
            Researched = water.FishingResearched,
            BaseCost = water.ResearchBaseCost,
            Progress = water.ResearchProgress,
            CostFactor = water.ResearchCostFactor,
            ResearcherSpeeds = [.. water.ResearcherSpeeds],
            PointsPerWorkTick = water.ResearchPointsPerWorkTick,
            Difficulty = water.ResearchDifficultySpeedFactor,
        });
        var fishers = new List<Fisher>(water.Fishers.Count);
        foreach (var f in water.Fishers)
        {
            fishers.Add(new Fisher(f.FishingYield, f.FishingSpeed));
        }
        var regions = new List<FishableRegion>();
        foreach (var row in water.Regions)
        {
            var cells = new List<FishingCell>(row.Cells.Count);
            foreach (var c in row.Cells)
            {
                cells.Add(new FishingCell { Cell = c.Cell, Reachable = c.Reachable, DistanceSquared = c.DistanceSquared });
            }
            var proposed = FishingFactsRules.FishingFootprint(cells, fishers.Count);
            var zones = new List<FishingZoneState>(row.Zones.Count);
            foreach (var z in row.Zones)
            {
                zones.Add(new FishingZoneState(z.Allowed, z.DoForever, z.HasFishableCells));
            }
            regions.Add(new FishableRegion
            {
                Root = row.Root,
                Population = row.Population,
                MaxPopulation = row.MaxPopulation,
                Zoned = row.Zoned,
                Frozen = row.Frozen,
                NutritionPerFish = row.NutritionPerFish,
                DistanceSquared = row.NearestDistanceSquared,
                ProposedCells = proposed,
                Reachable = FishingFactsRules.FishingReachable(row.Reachable, row.Zoned, proposed.Count, fishers.Count),
                Delivering = FishingFactsRules.FishingDelivering(zones),
                Crew = FishingFactsRules.FishingCrewAt(fishers, row.YieldCurveValue, row.NutritionPerFish, water.BaseFishingDurationTicks),
            });
        }
        return new FishableWater { Research = research, Regions = regions };
    }
}
