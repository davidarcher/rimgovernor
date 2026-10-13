using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_races.go: every animal race of the catalog, a ThingDef with RaceProperties that the game's own race code
// calls an animal, built from the defs, the stat source and the game-computed race facts. A catalog without race facts
// has no races.

public sealed partial class DefinitionCatalog
{
    private const string StatCarryingCapacity = "CarryingCapacity";
    private const string StatWildness = "Wildness";
    private const string StatMinimumHandlingSkill = "MinimumHandlingSkill";
    private const string StatLeatherAmount = "LeatherAmount";

    /// <summary>Every animal race of the catalog, built once.</summary>
    public P.AnimalRaceCatalog AnimalRaces() => _races.Value;

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
        var (mechanoid, insect) = RaceFlags(name);
        var ages = RaceStageAgesOf(props);
        var race = new P.AnimalRace
        {
            Def = new P.Resource(name),
            Predator = props.predator,
            BodySize = Known((double)props.baseBodySize),
            LifeExpectancy = Known((double)props.lifeExpectancy),
            ManhunterOnTameFail = Known((double)props.manhunterOnTameFailChance),
            ManhunterOnDamage = Known((double)props.manhunterOnDamageChance),
            Edible = new([.. facts.EdibleDefs]),
            Mechanoid = mechanoid,
            Insect = insect,
            Trainables = new([.. RaceTrainables(props)]),

            // A pest is a tree eater: RaceProperties.Eats(FoodTypeFlags.Tree).
            Pest = (props.foodType & FoodTypeFlags.Tree) != 0,
            EatsPlant = (props.foodType & FoodTypeFlags.Plant) != 0,
            GestationDays = props.gestationPeriodDays,
            Trainability = props.trainability is { } trainability ? Known(trainability.defName) : default,
            CarryingCapacity = ShownFact(name, StatCarryingCapacity),
            Wildness = ShownFact(name, StatWildness),
            MarketValue = ShownFact(name, StatNames.MarketValue),
            MateMtbHours = Known((double)props.mateMtbHours),
            AdultMinAgeTicks = Known(ages.Adult),
            ReproductiveMinAgeTicks = ages.Reproductive is { } r ? Known(r) : default,
            MilkableMinAgeTicks = ages.Milkable is { } m ? Known(m) : default,
            ShearableMinAgeTicks = ages.Shearable is { } s ? Known(s) : default,
            LifeStages = new([.. RaceLifeStages(props)]),
            LitterSize = LitterSizeMean(props.litterSizeCurve),
            TamenessCanDecay = Known(facts.TamenessCanDecay),
            TamenessDecayPeriodTicks = Known(facts.TamenessDecayPeriodTicks),
            TameChanceFactor = Known((double)facts.TameChanceFactor),
            AdultFeedPerDay = facts.AdultFeedPerDay is > 0 and { } feed ? Known(feed) : default,
            CombatPower = power.TryGetValue(name, out var p) ? Known(p) : default,
        };

        var (skill, skillShown) = ShownStatValue(name, string.Empty, StatMinimumHandlingSkill);
        if (skillShown && skill >= 0)
        {
            race = race with { MinimumHandlingSkill = Known((int)Math.Round((double)skill, MidpointRounding.AwayFromZero)) };
        }

        var butchery = new List<P.SourceProduct>();
        if (props.leatherDef is { } leather)
        {
            var (amount, shown) = ShownStatValue(name, string.Empty, StatLeatherAmount);
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
            var (amount, shown) = ShownStatValue(name, string.Empty, StatMeatAmount);
            if (!shown)
            {
                throw new CatalogException($"race {name} has meat {meat} but the stat table shows no {StatMeatAmount}");
            }

            race = race with { MeatDef = new P.Resource(meat), MeatAmount = Known((double)amount), MeatNutritionPerUnit = NutritionFact(meat) };
        }

        var products = RaceProducts(row).Select(product => product.Kind == "wool" ? product : product with { NutritionPerUnit = NutritionFact(product.Def.Value) }).ToList();
        race = race with { Products = new([.. products]) };

        var feedItems = new List<P.RaceFeedItem>();
        foreach (var item in produced.Where(item => facts.EdibleDefs.Contains(item)))
        {
            var (nutrition, shown) = ShownStatValue(item, string.Empty, StatNames.Nutrition);
            if (shown && nutrition > 0)
            {
                feedItems.Add(new P.RaceFeedItem(new P.Resource(item), nutrition));
            }
        }

        return race with { FeedItems = new([.. feedItems]) };
    }

    private Fact<double> ShownFact(string def, string stat)
    {
        var (value, shown) = ShownStatValue(def, string.Empty, stat);
        return shown ? Known((double)value) : default;
    }

    private Fact<double> NutritionFact(string def)
    {
        var (n, shown) = ShownStatValue(def, string.Empty, StatNames.Nutrition);
        return shown && n > 0 ? Known((double)n) : Unknown<double>();
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
    // curve, never below the one child a birth gives. Unknown for a curve Rand.ByCurve rejects (fewer than three points,
    // or ends not at y = 0).
    private static Fact<double> LitterSizeMean(SimpleCurve? curve)
    {
        if (curve is null)
        {
            return Known(1.0);
        }

        var points = curve.Points;
        var n = points.Count;
        if (n < 3 || points[0].y != 0 || points[n - 1].y != 0)
        {
            return Unknown<double>();
        }

        double area = 0, moment = 0;
        for (var i = 0; i < n - 1; i++)
        {
            double x0 = points[i].x, y0 = points[i].y, x1 = points[i + 1].x, y1 = points[i + 1].y;
            if (y0 < 0)
            {
                return Unknown<double>();
            }

            area += (x1 - x0) * (y0 + y1);
            moment += (x1 - x0) * ((x0 * ((2 * y0) + y1)) + (x1 * (y0 + (2 * y1))));
        }

        return area <= 0 ? Unknown<double>() : Known(Math.Max(1, moment / area / 3));
    }

    // The game's animal interaction job constants.
    private static P.AnimalInteraction AnimalInteraction() => new()
    {
        TalkTicks = PositiveFact(GameConstants.AnimalTalkDuration),
        FeedTicks = PositiveFact(GameConstants.AnimalFeedDuration),
        Feeds = PositiveFact(JobDriver_InteractAnimal.FeedCount),
        MinTrainIntervalTicks = PositiveFact(TrainableUtility.MinTrainInterval),
        FeedNutritionFraction = PositiveFact(GameConstants.AnimalNutritionPercentagePerFeed),
        FeedNutritionCap = PositiveFact(GameConstants.AnimalMaxMinNutritionPerFeed),
    };

    private static Fact<int> PositiveFact(int value) => value > 0 ? Known(value) : default;

    private static Fact<double> PositiveFact(float value) => value > 0 ? Known((double)value) : default;

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
            products.Add(new P.RaceProduct { Kind = "milk", Def = new P.Resource(milk.defName), Amount = Known((double)milkable.milkAmount), IntervalDays = Known((double)milkable.milkIntervalDays) });
        }

        if (row.GetCompProperties<CompProperties_Shearable>() is { woolDef: { } wool } shearable)
        {
            products.Add(new P.RaceProduct { Kind = "wool", Def = new P.Resource(wool.defName), Amount = Known((double)shearable.woolAmount), IntervalDays = Known((double)shearable.shearIntervalDays) });
        }

        if (row.GetCompProperties<CompProperties_EggLayer>() is { } egg && (egg.eggUnfertilizedDef ?? egg.eggFertilizedDef) is { } eggDef)
        {
            var product = new P.RaceProduct
            {
                Kind = "eggs",
                Def = new P.Resource(eggDef.defName),
                Amount = Known((egg.eggCountRange.min + egg.eggCountRange.max) * 0.5),
                IntervalDays = Known((double)egg.eggLayIntervalDays),
                FertilizationCountMax = egg.eggFertilizationCountMax,
                FemaleOnly = egg.eggLayFemaleOnly,
            };
            if (egg.eggFertilizedDef is { } fertilized)
            {
                var hatcher = fertilized.GetCompProperties<CompProperties_Hatcher>()
                    ?? throw new CatalogException($"race {row.defName} lays fertilized egg {fertilized.defName} with no hatcher comp");
                product = product with
                {
                    FertilizedDef = new P.Resource(fertilized.defName),
                    HatchDays = Known((double)hatcher.hatcherDaystoHatch),
                    HatchPawn = new P.Resource(hatcher.hatcherPawn?.defName ?? string.Empty),
                };
            }

            products.Add(product);
        }

        if (row.GetCompProperties<CompProperties_Spawner>() is { thingToSpawn: { } spawned } spawner)
        {
            var ticks = (spawner.spawnIntervalRange.min + spawner.spawnIntervalRange.max) * 0.5;
            products.Add(new P.RaceProduct { Kind = "spawner", Def = new P.Resource(spawned.defName), Amount = Known((double)spawner.spawnCount), IntervalDays = Known(ticks / GameTime.TicksPerDay) });
        }

        return products;
    }
}
