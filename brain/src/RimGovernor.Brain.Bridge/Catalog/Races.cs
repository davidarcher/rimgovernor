using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Every animal race of the catalog, a ThingDef with RaceProperties that the game's own race code
// calls an animal, built from the defs, the def stat table and the game-computed race facts. A catalog without race facts
// has no races.

public sealed partial class DefinitionCatalog
{
    /// <summary>Every animal race of the catalog, built once.</summary>
    public P.AnimalRaceCatalog AnimalRaces() => _races.Value;

    /// <summary>A pawn def's base body size (RaceProperties.baseBodySize); a def without race properties fails the read.</summary>
    public double BaseBodySize(string pawnDef) =>
        FindThing(pawnDef)?.race is { } props ? props.baseBodySize : throw new CatalogException($"pawn def {pawnDef} without race properties");

    private P.AnimalRaceCatalog BuildAnimalRaces()
    {
        var races = new Dictionary<P.Resource, P.AnimalRace>();
        var catalog = new P.AnimalRaceCatalog { Races = races, Interaction = AnimalInteraction() };
        if (_raceFacts.Count == 0)
        {
            return catalog;
        }

        var power = CombatPowers();
        var produced = ProducedIngestibles();
        foreach (var name in _raceFacts.Keys.Order(StringComparer.Ordinal))
        {
            var row = FindThing(name);
            if (row?.race is not { } props)
            {
                throw new CatalogException($"race facts for def {name} without race properties");
            }

            // A corpse def shares its pawn's RaceProperties; the race is the pawn def.
            if (row.category != ThingCategory.Pawn || !RaceIsAnimal(name, props))
            {
                continue;
            }

            var race = AnimalRace(name, row, _raceFacts[name], power, produced);
            races[race.Def] = race;
        }

        return catalog;
    }

    private P.AnimalRace AnimalRace(string name, ThingDef row, RaceGameFacts facts, Dictionary<string, double> power, IReadOnlyList<string> produced)
    {
        var props = row.race;
        var ages = RaceStageAgesOf(props);
        var race = new P.AnimalRace
        {
            Def = new P.Resource(name),
            Predator = props.predator,
            BodySize = Known((double)props.baseBodySize),
            LifeExpectancy = props.lifeExpectancy,
            ManhunterOnTameFail = props.manhunterOnTameFailChance,
            ManhunterOnDamage = props.manhunterOnDamageChance,
            Edible = new([.. facts.EdibleDefs]),
            Trainables = new([.. RaceTrainables(props)]),

            // A pest is a tree eater: RaceProperties.Eats(FoodTypeFlags.Tree).
            Pest = (props.foodType & FoodTypeFlags.Tree) != 0,
            EatsPlant = (props.foodType & FoodTypeFlags.Plant) != 0,
            GestationDays = props.gestationPeriodDays,
            CarryingCapacity = PlannerStatValue(name, string.Empty, CatalogStats.CarryingCapacity),
            Wildness = PlannerStatValue(name, string.Empty, CatalogStats.Wildness),
            MinimumHandlingSkill = (int)Math.Round((double)PlannerStatValue(name, string.Empty, CatalogStats.MinimumHandlingSkill), MidpointRounding.AwayFromZero),
            MateMtbHours = props.mateMtbHours,
            AdultMinAgeTicks = ages.Adult,
            Reproductive = ages.Reproductive is { } r ? new P.RaceStage.Reaches(r) : new P.RaceStage.Never(),
            Milkable = ages.Milkable is { } m ? new P.RaceStage.Reaches(m) : new P.RaceStage.Never(),
            LifeStages = new([.. RaceLifeStages(props)]),
            LitterSize = LitterSizeMean(props.litterSizeCurve),
            TameChanceFactor = facts.TameChanceFactor,
            AdultFeed = facts.AdultFeedPerDay is > 0 and { } feed ? new P.RaceFeed.Eats(feed) : new P.RaceFeed.NoNeed(),
            CombatPower = power.TryGetValue(name, out var p) ? new P.RaceCombat.Rated(p) : new P.RaceCombat.Unrated(),
        };


        var butchery = new List<P.SourceProduct>();
        if (props.leatherDef is { } leather)
        {
            var (amount, shown) = ShownStatValue(name, string.Empty, CatalogStats.LeatherAmount);
            if (shown && amount > 0)
            {
                butchery.Add(new P.SourceProduct(new P.Resource(leather.defName), amount));
            }
        }

        foreach (var product in row.butcherProducts.Seq())
        {
            if (product.thingDef is { } def && product.count > 0)
            {
                butchery.Add(new P.SourceProduct(new P.Resource(def.defName), product.count * (product.chance ?? 1f)));
            }
        }

        race = race with { Butchery = new([.. butchery]) };

        if (RaceMeatDef(row) is { Length: > 0 } meat)
        {
            var (amount, shown) = ShownStatValue(name, string.Empty, CatalogStats.MeatAmount);
            if (!shown)
            {
                throw new CatalogException($"race {name} has meat {meat} but the stat table shows no {StatMeatAmount}");
            }

            var (nutrition, nutritionShown) = ShownStatValue(meat, string.Empty, CatalogStats.Nutrition);
            if (!nutritionShown || !(nutrition > 0))
            {
                throw new CatalogException($"race {name} has meat {meat} but the stat table shows it no positive nutrition");
            }

            race = race with { Meat = new P.RaceMeat.Yields(amount, nutrition) };
        }

        var products = RaceProducts(row).Select(product => product.Kind == "wool" ? product : product with { Food = ProductFoodOf(product.Def.Value) }).ToList();
        race = race with { Products = new([.. products]) };

        var feedItems = new List<P.RaceFeedItem>();
        foreach (var item in produced.Where(item => facts.EdibleDefs.Contains(item)))
        {
            var (nutrition, shown) = ShownStatValue(item, string.Empty, CatalogStats.Nutrition);
            if (shown && nutrition > 0)
            {
                feedItems.Add(new P.RaceFeedItem(new P.Resource(item), nutrition));
            }
        }

        return race with { FeedItems = new([.. feedItems]) };
    }


    // A product the stat table shows no positive nutrition for (chemfuel) feeds no one.
    private P.RaceProductFood ProductFoodOf(string def)
    {
        var (n, shown) = ShownStatValue(def, string.Empty, CatalogStats.Nutrition);
        return shown && n > 0 ? new P.RaceProductFood.Feeds(n) : new P.RaceProductFood.Inedible();
    }

    // The race's lifeStageAges with each stage's hunger rate factor; empty when an entry or its LifeStageDef is absent.
    private static List<P.RaceLifeStage> RaceLifeStages(RaceProperties props)
    {
        var stages = new List<P.RaceLifeStage>();
        foreach (var age in props.lifeStageAges.Seq())
        {
            if (age?.def is not { } stage)
            {
                return [];
            }

            stages.Add(new P.RaceLifeStage((long)Math.Round((double)age.minAge * GameTime.TicksPerYear, MidpointRounding.AwayFromZero), stage.hungerRateFactor, stage.bodySizeFactor));
        }

        return stages;
    }

    // The mean litter of a birth, as Hediff_Pregnant rolls it: one child without a curve, else Rand.ByCurveAverage of the
    // curve, never below the one child a birth gives. Unrollable for a curve Rand.ByCurve rejects (fewer than three points,
    // or ends not at y = 0).
    private static P.RaceLitter LitterSizeMean(SimpleCurve? curve)
    {
        if (curve is null)
        {
            return new P.RaceLitter.Mean(1);
        }

        var points = curve.Points;
        var n = points.Count;
        if (n < 3 || points[0].y != 0 || points[n - 1].y != 0)
        {
            return new P.RaceLitter.Unrollable();
        }

        double area = 0, moment = 0;
        for (var i = 0; i < n - 1; i++)
        {
            double x0 = points[i].x, y0 = points[i].y, x1 = points[i + 1].x, y1 = points[i + 1].y;
            if (y0 < 0)
            {
                return new P.RaceLitter.Unrollable();
            }

            area += (x1 - x0) * (y0 + y1);
            moment += (x1 - x0) * ((x0 * ((2 * y0) + y1)) + (x1 * (y0 + (2 * y1))));
        }

        return area <= 0 ? new P.RaceLitter.Unrollable() : new P.RaceLitter.Mean(Math.Max(1, moment / area / 3));
    }

    // The game's animal interaction job constants, each positive.
    private static P.AnimalInteraction AnimalInteraction() => new()
    {
        TalkTicks = Positive(GameConstants.AnimalTalkDuration, nameof(GameConstants.AnimalTalkDuration)),
        FeedTicks = Positive(GameConstants.AnimalFeedDuration, nameof(GameConstants.AnimalFeedDuration)),
        Feeds = Positive(JobDriver_InteractAnimal.FeedCount, nameof(JobDriver_InteractAnimal.FeedCount)),
    };

    private static int Positive(int value, string name) => value > 0 ? value : throw new CatalogException($"animal interaction constant {name} is {value}, not positive");

    // The highest PawnKindDef.combatPower of each race.
    private Dictionary<string, double> CombatPowers()
    {
        var power = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var kind in Rows<PawnKindDef>().Values.Where(kind => kind.race is not null))
        {
            var race = kind.race.defName;
            if (!power.TryGetValue(race, out var best) || kind.combatPower > best)
            {
                power[race] = kind.combatPower;
            }
        }

        return power;
    }

    // The defs some recipe produces, sorted.
    private IReadOnlyList<string> ProducedIngestibles() =>
        [.. Rows<RecipeDef>().Values.SelectMany(recipe => recipe.products.Seq()).Where(product => product.thingDef is not null).Select(product => product.thingDef.defName).Distinct().Order(StringComparer.Ordinal)];

    // The periodic yields of the race's comps: milk, wool, eggs and spawned items (chemfuel).
    private static List<P.RaceProduct> RaceProducts(ThingDef row)
    {
        var products = new List<P.RaceProduct>();
        if (row.GetCompProperties<CompProperties_Milkable>() is { milkDef: { } milk } milkable)
        {
            products.Add(new P.RaceProduct { Kind = "milk", Def = new P.Resource(milk.defName), Amount = milkable.milkAmount, IntervalDays = milkable.milkIntervalDays });
        }

        if (row.GetCompProperties<CompProperties_Shearable>() is { woolDef: { } wool } shearable)
        {
            products.Add(new P.RaceProduct { Kind = "wool", Def = new P.Resource(wool.defName), Amount = shearable.woolAmount, IntervalDays = shearable.shearIntervalDays });
        }

        if (row.GetCompProperties<CompProperties_EggLayer>() is { } egg && (egg.eggUnfertilizedDef ?? egg.eggFertilizedDef) is { } eggDef)
        {
            var product = new P.RaceProduct
            {
                Kind = "eggs",
                Def = new P.Resource(eggDef.defName),
                Amount = (egg.eggCountRange.min + egg.eggCountRange.max) * 0.5,
                IntervalDays = egg.eggLayIntervalDays,
                FertilizationCountMax = egg.eggFertilizationCountMax,
                FemaleOnly = egg.eggLayFemaleOnly,
            };
            if (egg.eggFertilizedDef is { } fertilized)
            {
                product = product with { FertilizedDef = new P.Resource(fertilized.defName) };
            }

            products.Add(product);
        }

        if (row.GetCompProperties<CompProperties_Spawner>() is { thingToSpawn: { } spawned } spawner)
        {
            var ticks = (spawner.spawnIntervalRange.min + spawner.spawnIntervalRange.max) * 0.5;
            products.Add(new P.RaceProduct { Kind = "spawner", Def = new P.Resource(spawned.defName), Amount = spawner.spawnCount, IntervalDays = ticks / GameTime.TicksPerDay });
        }

        return products;
    }
}
