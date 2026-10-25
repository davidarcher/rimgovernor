using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Bridge;

public static class FoodChannelsReads
{
    /// <summary>Called only after the colony boundary validator has checked the section.</summary>
    internal static FoodChannelPlanning ColonyFoodChannels(RimGovernor.Brain.GameFacts.Observations.FoodChannelsFacts v, Pawns pawns, AnimalRaceCatalog races)
    {
        var pasture = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in v.PastureTerrain)
        {
            pasture[row.TerrainDef] = row.WorstQuadrumPerDay;
        }
        var slaughter = new List<SlaughterFoodAnimal>();
        foreach (var row in v.Slaughter)
        {
            // A race the catalog does not hold is the zero row: no meat, no plant feed, no breeding.
            races.Race(new Resource(row.Race), out var race);
            slaughter.Add(SlaughterFood(row, race, pawns));
        }
        var products = new List<AnimalProduct>();
        foreach (var row in v.Gatherable)
        {
            if (row.Yield is RimGovernor.Brain.GameFacts.Observations.AnimalYield.Producing)
            {
                products.Add(new AnimalProduct.Gatherable(row) { Feed = ProductFeed(row.PawnId, row.Race, pawns, races) });
            }
        }
        foreach (var row in v.EggLayer)
        {
            if (row.Yield is RimGovernor.Brain.GameFacts.Observations.AnimalYield.Producing)
            {
                products.Add(new AnimalProduct.EggLayer(row) { Feed = ProductFeed(row.PawnId, row.Race, pawns, races) });
            }
        }
        return new FoodChannelPlanning
        {
            Grazing = v.Grazing,
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
    private static SlaughterFoodAnimal SlaughterFood(RimGovernor.Brain.GameFacts.Observations.FoodSlaughterAnimal row, AnimalRace race, Pawns pawns)
    {
        // A race without meat yields no food.
        var per = race.Meat is RaceMeat.Yields { NutritionPerUnit: var nutrition } ? nutrition : 0;
        var days = race.GestationDays;
        foreach (var product in race.Products)
        {
            if (product.Kind == "eggs")
            {
                days = product.IntervalDays;
            }
        }
        return new SlaughterFoodAnimal
        {
            Observed = row,
            MeatNutrition = row.MeatAmount * per,
            Feed = FeedOf(race, AnimalStateOf(pawns, row.PawnId)),
            Breeding = days > 0 ? new AnimalBreeding.Every(days) : new AnimalBreeding.Never(),
        };
    }

    /// <summary>A producing animal's own plant feed, which its product channel nets out.</summary>
    private static AnimalFeed ProductFeed(string pawn, string raceName, Pawns pawns, AnimalRaceCatalog races)
    {
        races.Race(new Resource(raceName), out var race);
        return FeedOf(race, AnimalStateOf(pawns, pawn));
    }

    /// <summary>The animal block of a food channel's animal; one the pawn table does not hold as an animal is a contract break.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.AnimalState AnimalStateOf(Pawns pawns, string id) =>
        pawns.TryGetValue(id, out var row) && row?.AnimalState is RimGovernor.Brain.GameFacts.Observations.PawnAnimalState.Animal { State: var state }
            ? state
            : throw new ObservationContractException($"food channel animal {id} has no animal row in the pawn table");

    /// <summary>
    /// The plants an animal eats a day in its current life stage: the race's adult feed scaled by the stage's
    /// hunger factor against the adult stage's. A race that eats no plants, or has no food need, eats NoPlants; a
    /// life stage the race's catalog stages do not hold is a contract break.
    /// </summary>
    internal static AnimalFeed FeedOf(AnimalRace race, RimGovernor.Brain.GameFacts.Observations.AnimalState state)
    {
        if (!race.EatsPlant || race.AdultFeed is not RaceFeed.Eats { PerDay: var adult })
        {
            return new AnimalFeed.NoPlants();
        }
        var stages = race.LifeStages;
        var index = state.LifeStageIndex;
        if (index < 0 || index >= stages.Count || !(stages[^1].HungerRateFactor > 0))
        {
            throw new ObservationContractException($"race {race.Def.Value} holds no life stage {index} with a hunger rate");
        }
        return new AnimalFeed.Plants(adult * stages[index].HungerRateFactor / stages[^1].HungerRateFactor);
    }

    /// <summary>Applies the policy decisions to native's raw fishing facts: research state, footprint, reachability verdict, delivery and crew.</summary>
    private static FishingWaterReview ColonyFishableWater(RimGovernor.Brain.GameFacts.Observations.FishableWater water)
    {
        var research = FishingFactsRules.FishingResearchState(water);
        var fishers = water.Fishers;
        var regions = new List<FishingRegionReview>();
        foreach (var row in water.Regions)
        {
            var proposed = FishingFactsRules.FishingFootprint(row.Cells, fishers.Count);
            regions.Add(new FishingRegionReview
            {
                Observed = row,
                ProposedCells = proposed,
                Reachable = FishingFactsRules.FishingReachable(row.Reachable, row.Zoned, proposed.Count, fishers.Count),
                Delivering = FishingFactsRules.FishingDelivering(row.Zones),
                Crew = FishingFactsRules.FishingCrewAt(water.Fishers, row.YieldCurveValue, row.NutritionPerFish, water.BaseFishingDurationTicks),
            });
        }
        return new FishingWaterReview { Research = research, Regions = regions };
    }
}
