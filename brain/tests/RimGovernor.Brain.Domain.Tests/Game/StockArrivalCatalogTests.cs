using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3868: the catalog's stock recipes, what makes a part an install recipe puts back, and the production way from the defs to a
// replacement's wait: a fresh colony without the bench waits on trade, which is named; one with it crafts the part at its rate.
public sealed class StockArrivalCatalogTests
{
    private static readonly StatDef Potency = new() { defName = "MedicalPotency" };
    private static readonly StatDef Labor = new() { defName = "GeneralLaborSpeed" };
    private static readonly SkillDef CraftingSkill = new() { defName = "Crafting" };
    private static readonly WorkTypeDef Crafting = new() { defName = "Crafting" };
    private static readonly ThingDef Plasteel = new() { defName = "Plasteel" };
    private static readonly ThingDef Herbal = new() { defName = "MedicineHerbal", statBases = [new RimWorld.StatModifier { stat = Potency, value = 0.6f }] };
    private static readonly ThingDef BionicArm = new() { defName = "BionicArm" };
    private static readonly BodyPartDef Arm = new() { defName = "Arm" };

    private static IngredientCount Slot(ThingDef def, float count)
    {
        var filter = new ThingFilter();
        filter.SetAllow(def, true);
        var slot = new IngredientCount { filter = filter };
        slot.SetBaseCount(count);
        return slot;
    }

    private static DefinitionCatalog Catalog()
    {
        var install = new RecipeDef
        {
            defName = "InstallBionicArm", workerClass = typeof(Recipe_InstallArtificialBodyPart), appliedOnFixedBodyParts = [Arm],
            ingredients = [Slot(BionicArm, 1), Slot(Herbal, 2)],
        };
        var make = new RecipeDef
        {
            defName = "Make_BionicArm", workAmount = 10_000, products = [new ThingDefCountClass(BionicArm, 1)], ingredients = [Slot(Plasteel, 15)],
            workSpeedStat = Labor, skillRequirements = [new Verse.SkillRequirement { skill = CraftingSkill, minLevel = 8 }],
        };
        var bench = new ThingDef
        {
            defName = "FabricationBench", category = Verse.ThingCategory.Building, thingClass = typeof(Building_WorkTable),
            designationCategory = new DesignationCategoryDef { defName = "Production" }, recipes = [make],
        };
        return new DefinitionCatalog(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, Potency, Labor, CraftingSkill, Crafting, Plasteel, Herbal, BionicArm, Arm,
                install, make, bench,
                new WorkGiverDef { defName = "DoBillsFabricate", workType = Crafting, giverClass = typeof(WorkGiver_DoBill), fixedBillGiverDefs = [bench] }],
            DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));
    }

    [Fact]
    public void TheStockRecipesAreThoseMakingAnInstallRecipesPartAtTheBenchesItsBillGiverServes()
    {
        var recipe = Assert.Single(Catalog().StockRecipes(new HashSet<string>()));
        Assert.Equal(("Make_BionicArm", new Resource("BionicArm"), 1L, 10_000.0, "GeneralLaborSpeed", StockArrivalRules.BenchSpeedStat),
            (recipe.Recipe, recipe.Product, recipe.Batch, recipe.WorkAmount, recipe.WorkSpeedStat, recipe.BenchSpeedStat));
        Assert.Equal([new StockRecipeBench("FabricationBench", WorkType.Crafting)], recipe.Benches);
        Assert.Equal([new Policy.SkillRequirement("Crafting", 8)], recipe.Skills);
        Assert.Equal([[new Amount(new Resource("Plasteel"), 15)]], recipe.Ingredients);
    }

    [Fact]
    public void AFreshColonyWithoutTheBenchWaitsOnTradeAndOneWithItCraftsThePartAtItsRate()
    {
        // The production way (#3822): the catalog's recipes, the bills census's benches and a crafting colonist as the read boundary
        // fills them, to the replacement the autopilot would book for a lost arm with no arm on the map.
        var recipes = Catalog().StockRecipes(new HashSet<string>());
        StockCrafter[] crafters = [new("Anna", 8, [WorkType.Crafting], [new SkillLevel("Crafting", 10)], [new WorkSpeedRead("GeneralLaborSpeed", 1)])];
        var stock = new Dictionary<Resource, long> { [new Resource("Plasteel")] = 100 };
        var replacement = new OrganReplacementRead("Arm", "InstallBionicArm")
        {
            PartThing = "BionicArm", PartOnMap = false, MedicineOnMap = true, MedicineCount = 2, EligibleDoctors = 1, SuccessChance = 0.95, DoctorSuccessChance = 0.95,
        };

        var fresh = StockArrivalRules.Craft(recipes, [], crafters, stock) with { Medicines = [new Resource("MedicineHerbal")] };
        Assert.Empty(fresh.Sources);
        var (pick, reason, _) = SurgeryRules.RestorePick([(replacement, 1.0)], fresh, 30);
        Assert.Equal((null, SurgeryWantReason.PartShort), (pick, reason));

        // A fabrication bench at 1x: 8 hours × 2500 ticks ÷ 10 000 work, 2 arms a day; 100 plasteel pays for 6.
        var built = StockArrivalRules.Craft(recipes, [new StockBench("FabricationBench", true, 1)], crafters, stock) with { Medicines = [new Resource("MedicineHerbal")] };
        var source = Assert.Single(built.Sources);
        Assert.Equal((new Resource("BionicArm"), 2.0, 6L), (source.Resource, source.PerDay, source.Units));
        (pick, reason, var wait) = SurgeryRules.RestorePick([(replacement, 1.0)], built, 30);
        Assert.Equal("InstallBionicArm", pick!.Recipe);
        Assert.Equal(0.5, wait!.Expected(30), 12);
    }

    // #3869: a vanilla-shaped storyteller's trader comps, an outlander caravan kind carrying bionics by tag and a market value.
    private static readonly IncidentCategoryDef FactionArrival = new() { defName = "FactionArrival" };
    private static readonly IncidentCategoryDef OrbitalVisitor = new() { defName = "OrbitalVisitor" };
    private static readonly IncidentDef Caravan = new() { defName = "TraderCaravanArrival", category = FactionArrival, workerClass = typeof(IncidentWorker_TraderCaravanArrival) };
    private static readonly IncidentDef Orbital = new() { defName = "OrbitalTraderArrival", category = OrbitalVisitor, workerClass = typeof(IncidentWorker_OrbitalTraderArrival) };
    private static readonly IncidentDef Visitors = new() { defName = "VisitorGroup", category = FactionArrival, workerClass = typeof(IncidentWorker_VisitorGroup) };

    private static StorytellerDef Cassandra() => new()
    {
        defName = "Cassandra",
        comps =
        [
            new StorytellerCompProperties_FactionInteraction { incident = Caravan, minDaysPassed = 5, baseIncidentsPerYear = 5, minSpacingDays = 6 },
            new StorytellerCompProperties_FactionInteraction { incident = Visitors, minDaysPassed = 3, baseIncidentsPerYear = 4 },
            new StorytellerCompProperties_OnOffCycle { incident = Orbital, onDays = 7, offDays = 8, numIncidentsRange = new FloatRange(1, 1) },
        ],
    };

    private static TraderKindDef Outlander()
    {
        var implants = new StockGenerator_Tag { tradeTag = "TechHediff", countRange = new IntRange(1, 1) };
        GameMembers.StockTagDefCount(implants) = new IntRange(3, 3);
        return new TraderKindDef { defName = "Caravan_Outlander_Exotic", stockGenerators = [implants, new StockGenerator_BuyTradeTag { tag = "TechHediff" }] };
    }

    private static DefinitionCatalog TradeCatalog()
    {
        ThingDef arm = new() { defName = "BionicArm", tradeTags = ["TechHediff"] }, leg = new() { defName = "BionicLeg", tradeTags = ["TechHediff"] };
        ThingDef silver = new() { defName = "Silver" }, herbal = new() { defName = "MedicineHerbal", statBases = [new RimWorld.StatModifier { stat = Potency, value = 0.6f }] };
        var install = new RecipeDef
        {
            defName = "InstallBionicArm", workerClass = typeof(Recipe_InstallArtificialBodyPart), appliedOnFixedBodyParts = [Arm], ingredients = [Slot(arm, 1), Slot(herbal, 2)],
        };
        var shown = new DefStatTable.Value(1000f, true, null);
        var stats = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(arm, null)] = new([], null), [(leg, null)] = new([], null), [(herbal, null)] = new([], null) },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
            {
                [(arm, null, CatalogStats.MarketValue)] = shown, [(leg, null, CatalogStats.MarketValue)] = shown,
                [(herbal, null, CatalogStats.MarketValue)] = new(10f, true, null),
            },
            new Dictionary<BuildableDef, IReadOnlyList<string>>(),
            new HashSet<CatalogStat>(CatalogStats.All));
        return new DefinitionCatalog(
            [silver, new TerrainDef { defName = "Soil" }, Potency, Arm, arm, leg, herbal, install, FactionArrival, OrbitalVisitor, Caravan, Orbital, Visitors, Cassandra(), Outlander()],
            stats, new CatalogDerived("Silver", "Wort", 10f));
    }

    [Fact]
    public void TheCatalogDecodesTheTraderCompsAndTheKindsStockByClass()
    {
        var catalog = TradeCatalog();
        var cassandra = catalog.TradeSchedule().Storytellers["Cassandra"];
        Assert.Equal([new TradeVisitComp("TraderCaravanArrival", false, 5) { IncidentsPerDay = 5 / 60.0 }], cassandra.Comps.Where(c => !c.Orbital));
        var orbital = Assert.Single(cassandra.Comps, c => c.Orbital);
        Assert.Equal(("OrbitalTraderArrival", 7.0, 8.0), (orbital.Incident, orbital.Cycle!.OnDays, orbital.Cycle.OffDays));
        Assert.Equal(["StorytellerCompProperties_FactionInteraction:VisitorGroup"], cassandra.Unmodelled);

        // The tag generator draws 3 of its 2 tagged defs: each is picked; one each at 1000 × 1.4 × Normal. The buy generator makes nothing.
        var kind = catalog.TraderStock().Kinds["Caravan_Outlander_Exotic"];
        var offer = kind.Offers[new Resource("BionicArm")];
        Assert.Equal(1400, offer.UnitValue, 3);
        Assert.Equal([new StockDraw(2, 1, 3, 3, 1, 1, 0, 0, 1000)], offer.Draws);
        Assert.Equal(1, offer.AtLeast(1));
        Assert.Empty(kind.Unmodelled);
        Assert.False(kind.Offers.ContainsKey(new Resource("MedicineHerbal")));
    }

    [Fact]
    public void AFreshColonyWithoutAFabricationBenchHasAFiniteTradeWaitForABionicArm()
    {
        // The production way (#3822): the catalog's decoded comps and stock, the census as the read boundary fills it with the
        // native TradeVisitFacts, to the replacement the autopilot would book for a lost arm with no arm on the map and no bench.
        var catalog = TradeCatalog();
        var visits = new TradeVisitFacts
        {
            AllyFraction = 1, FullAllyFraction = -1, DaysSinceSettle = 0, CommsConsole = false, TradeBeacon = false, Negotiator = true, NegotiatorPriceImprovement = 0, PriceFactorLoss = 0,
            Incidents =
            [
                new TraderIncidentRead("OrbitalTraderArrival") { Sources = [new() { Kinds = [new TraderKindWeight("Caravan_Outlander_Exotic", 1)] }] },
                new TraderIncidentRead("TraderCaravanArrival") { Sources = [new() { Kinds = [new TraderKindWeight("Caravan_Outlander_Exotic", 1)] }] },
            ],
        };
        var census = ColonyFactsCoreTests.Snapshot() with
        {
            Threat = new ThreatSection { Observed = ColonyFactsCoreTests.QuietThreat with { Trade = visits } },
            Resources = [new Quantity { DefName = "Silver", Units = 2000 }],
        };
        var bills = new BillsSnapshot { Context = census.Context, Benches = [] };

        var stock = RimGovernor.Brain.Observation.StockArrivalReads.Of(catalog, new HashSet<string>(), bills, null, census, [new Resource("MedicineHerbal")]);
        var replacement = new OrganReplacementRead("Arm", "InstallBionicArm")
        {
            PartThing = "BionicArm", PartOnMap = false, MedicineOnMap = true, MedicineCount = 2, EligibleDoctors = 1, SuccessChance = 0.95, DoctorSuccessChance = 0.95,
        };
        var (pick, _, wait) = SurgeryRules.RestorePick([(replacement, 1.0)], stock, 30);

        // Caravans from day 5 at 5 a 60-day year, each carrying the arm, and 2000 silver pays its 1400: no orbital trader without comms.
        Assert.Equal("InstallBionicArm", pick!.Recipe);
        var rate = 5 / 60.0;
        Assert.Equal(5 + ((1 - Math.Exp(-rate * 25)) / rate), wait!.Expected(30), 9);
        Assert.Contains(TradeArrivalRules.OrbitalNoComms, stock.Findings);
        Assert.Contains(TradeArrivalRules.SilverIncomeUnmodelled, stock.Findings);

        // Without the native read the stock is unread and a wait on it throws.
        var unread = RimGovernor.Brain.Observation.StockArrivalReads.Of(catalog, new HashSet<string>(), bills, null, ColonyFactsCoreTests.Snapshot(), [new Resource("MedicineHerbal")]);
        Assert.Equal("stock_arrival_trade_facts", unread.Unread);
    }
}
