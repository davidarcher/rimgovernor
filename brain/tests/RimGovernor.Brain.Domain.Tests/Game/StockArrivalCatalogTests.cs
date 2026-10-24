using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimWorld;
using Verse;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

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

    // #3869, #3909: vanilla-shaped storytellers' trader comps, an outlander caravan kind carrying bionics by tag and a market value.
    private static readonly IncidentCategoryDef FactionArrival = new() { defName = "FactionArrival" };
    private static readonly IncidentCategoryDef OrbitalVisitor = new() { defName = "OrbitalVisitor" };
    private static readonly IncidentCategoryDef Misc = new() { defName = "Misc" };
    private static readonly IncidentCategoryDef ThreatBig = new() { defName = "ThreatBig" };
    private static readonly IncidentDef Caravan = new() { defName = "TraderCaravanArrival", category = FactionArrival, workerClass = typeof(IncidentWorker_TraderCaravanArrival) };
    private static readonly IncidentDef Orbital = new() { defName = "OrbitalTraderArrival", category = OrbitalVisitor, workerClass = typeof(IncidentWorker_OrbitalTraderArrival) };
    private static readonly IncidentDef Visitors = new() { defName = "VisitorGroup", category = FactionArrival, workerClass = typeof(IncidentWorker_VisitorGroup) };
    private static readonly IncidentDef Travelers = new() { defName = "TravelerGroup", category = FactionArrival, workerClass = typeof(IncidentWorker_TravelerGroup) };

    private static StorytellerDef Cassandra() => new()
    {
        defName = "Cassandra",
        comps =
        [
            new StorytellerCompProperties_FactionInteraction { incident = Caravan, minDaysPassed = 5, baseIncidentsPerYear = 5, minSpacingDays = 6 },
            new StorytellerCompProperties_FactionInteraction { incident = Visitors, minDaysPassed = 3, baseIncidentsPerYear = 4 },
            new StorytellerCompProperties_FactionInteraction { incident = Travelers, minDaysPassed = 3, baseIncidentsPerYear = 4 },
            new StorytellerCompProperties_OnOffCycle { incident = Orbital, onDays = 7, offDays = 8, numIncidentsRange = new FloatRange(1, 1) },
        ],
    };

    private static StorytellerDef Randy() => new()
    {
        defName = "Randy",
        comps =
        [
            new StorytellerCompProperties_RandomMain
            {
                minDaysPassed = 1, mtbDays = 1.35f, maxThreatBigIntervalDays = 13, minIncChancePopulationIntentFactor = 0.2f,
                categoryWeights =
                [
                    new() { category = Misc, weight = 3.5f }, new() { category = ThreatBig, weight = 1.4f }, new() { category = OrbitalVisitor, weight = 1.1f },
                    new() { category = FactionArrival, weight = 2.4f },
                ],
            },
        ],
    };

    // A tribal faction whose Peaceful groups may bring children.
    private static FactionDef Tribe() => new()
    {
        defName = "TribeCivil",
        pawnGroupMakers =
        [
            new PawnGroupMaker
            {
                kindDef = new PawnGroupKindDef { defName = "Peaceful" },
                options = [new PawnGenOption { kind = new PawnKindDef { defName = "Tribal_Child", pawnGroupDevelopmentStage = DevelopmentalStage.Child }, selectionWeight = 1 }],
            },
        ],
    };

    private static TraderKindDef Outlander()
    {
        var implants = new StockGenerator_Tag { tradeTag = "TechHediff", countRange = new IntRange(1, 1) };
        GameMembers.StockTagDefCount(implants) = new IntRange(3, 3);
        return new TraderKindDef { defName = "Caravan_Outlander_Exotic", stockGenerators = [implants, new StockGenerator_BuyTradeTag { tag = "TechHediff" }] };
    }

    // A caravan kind that sells 3 to 5 herbal medicine a visit.
    private static TraderKindDef Medic(ThingDef herbal)
    {
        var sale = new StockGenerator_SingleDef { countRange = new IntRange(3, 5) };
        GameMembers.SingleDefThing(sale) = herbal;
        return new TraderKindDef { defName = "Caravan_Outlander_Medic", stockGenerators = [sale] };
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
            [
                silver, new TerrainDef { defName = "Soil" }, Potency, Arm, arm, leg, herbal, install, FactionArrival, OrbitalVisitor, Misc, ThreatBig, Caravan, Orbital, Visitors,
                Travelers, Cassandra(), Randy(), Tribe(), Outlander(), Medic(herbal),
            ],
            stats, new CatalogDerived("Silver", "Wort", 10f));
    }

    [Fact]
    public void TheCatalogDecodesTheTraderCompsAndTheKindsStockByClass()
    {
        var catalog = TradeCatalog();
        var schedule = catalog.TradeSchedule();
        var cassandra = schedule.Storytellers["Cassandra"];
        // The caravans and visitors by faction interaction, the orbital traders by cycle; the travelers send no trader of their own.
        Assert.Equal(
            [
                new TradeVisitComp(TradeCompRate.FactionInteraction, 5) { Incident = "TraderCaravanArrival", IncidentsPerDay = 5 / 60.0 },
                new TradeVisitComp(TradeCompRate.FactionInteraction, 3) { Incident = "VisitorGroup", IncidentsPerDay = 4 / 60.0 },
            ],
            cassandra.Comps.Where(c => c.Rate == TradeCompRate.FactionInteraction));
        var orbital = Assert.Single(cassandra.Comps, c => c.Rate == TradeCompRate.OnOffCycle);
        Assert.Equal(("OrbitalTraderArrival", 7.0, 8.0), (orbital.Incident, orbital.Cycle!.OnDays, orbital.Cycle.OffDays));
        Assert.Empty(cassandra.Unmodelled);

        // Randy's RandomMain draws both trader categories, ThreatBig apart.
        var random = Assert.Single(schedule.Storytellers["Randy"].Comps);
        Assert.Equal((TradeCompRate.Mtb, 1.0, 1 / 1.35), (random.Rate, random.MinDaysPassed, random.IncidentsPerDay));
        var draw = random.Draw!;
        Assert.Equal((TradeDrawRule.RandomMain, 1.4, 13.0, 0.2), (draw.Rule, draw.ThreatBigWeight, draw.MaxThreatBigIntervalDays, draw.MinPopulationIntentFactor));
        Assert.Equal([new("Misc", 3.5, false), new("OrbitalVisitor", 1.1, true), new TradeCategoryWeight("FactionArrival", 2.4, true)], draw.Categories);
        Assert.Equal(new TradeIncident("FactionArrival", TradeWorker.Travelers, 0), schedule.Incidents["TravelerGroup"]);
        Assert.Equal(["TribeCivil"], schedule.ChildVisitorFactions);
        Assert.Equal([(0, 0.05), (1, 0.3), (3, 0.45)], schedule.PopulationChanceByIntent);

        // The tag generator draws 3 of its 2 tagged defs: each is picked; one each at 1000 × 1.4 × Normal. The buy generator makes nothing.
        var kind = catalog.TraderStock().Kinds["Caravan_Outlander_Exotic"];
        var offer = kind.Offers[new Resource("BionicArm")];
        Assert.Equal(1400, offer.UnitValue, 3);
        Assert.Equal([new StockDraw(2, 1, 3, 3, 1, 1, 0, 0, 1000)], offer.Draws);
        Assert.Equal(1, offer.AtLeast(1));
        Assert.Empty(kind.Unmodelled);
        Assert.False(kind.Offers.ContainsKey(new Resource("MedicineHerbal")));
    }

    private static TradeVisitFacts Visits(string kind = "Caravan_Outlander_Exotic") => new()
    {
        AllyFraction = 1, FullAllyFraction = -1, DaysSinceSettle = 0, CommsConsole = false, TradeBeacon = false, Negotiator = true, NegotiatorPriceImprovement = 0, PriceFactorLoss = 0,
        PopulationIntent = 1,
        Incidents =
        [
            new TraderIncidentRead("OrbitalTraderArrival") { Weight = 3, Sources = [new() { Kinds = [new TraderKindWeight(kind, 1)] }] },
            new TraderIncidentRead("TraderCaravanArrival") { Weight = 4, Sources = [new() { Faction = "OutlanderCivil", Kinds = [new TraderKindWeight(kind, 1)] }] },
            new TraderIncidentRead("TravelerGroup") { Weight = 4 },
            new TraderIncidentRead("VisitorGroup") { Weight = 4, Sources = [new() { Faction = "OutlanderCivil", Kinds = [new TraderKindWeight(kind, 1)] }] },
        ],
    };

    // Three colonists (the routine trade's reserve is 300 silver) and the silver they hold.
    private static StockArrival FreshStock(DefinitionCatalog catalog, string storyteller, long silver, string kind = "Caravan_Outlander_Exotic")
    {
        var census = ColonyFactsCoreTests.Snapshot() with
        {
            Threat = new ThreatSection { Observed = ColonyFactsCoreTests.QuietThreat with { Storyteller = storyteller, Trade = Visits(kind) } },
            Resources = [new Quantity { DefName = "Silver", Units = silver }],
            ColonistCount = 3,
        };
        var bills = new BillsSnapshot { Context = census.Context, Benches = [] };
        var pawns = new PawnSnapshot { Context = census.Context, Pawns = [] };
        return RimGovernor.Brain.Observation.StockArrivalReads.Of(catalog, new HashSet<string>(), bills, pawns, census, [new Resource("MedicineHerbal")]);
    }

    private static (double Expected, StockArrival Stock) ArmWait(DefinitionCatalog catalog, string storyteller)
    {
        var stock = FreshStock(catalog, storyteller, 2000);
        var replacement = new OrganReplacementRead("Arm", "InstallBionicArm")
        {
            PartThing = "BionicArm", PartOnMap = false, MedicineOnMap = true, MedicineCount = 2, EligibleDoctors = 1, SuccessChance = 0.95, DoctorSuccessChance = 0.95,
        };
        var (pick, _, wait) = SurgeryRules.RestorePick([(replacement, 1.0)], stock, 30);
        Assert.Equal("InstallBionicArm", pick!.Recipe);
        return (wait!.Expected(30), stock);
    }

    [Fact]
    public void AFreshColonyWithoutAFabricationBenchHasAFiniteTradeWaitForABionicArm()
    {
        // The production way (#3822): the catalog's decoded comps and stock, the census as the read boundary fills it with the
        // native TradeVisitFacts, to the replacement the autopilot would book for a lost arm with no arm on the map and no bench.
        var catalog = TradeCatalog();
        var (expected, stock) = ArmWait(catalog, "Cassandra");

        // Visitors from day 3 at 4 a 60-day year, trading with 0.75; caravans from day 5 at 5 a year; each carries the arm, and 2000
        // silver pays its 1400: no orbital trader without comms.
        double visitor = 4 / 60.0 * 0.75, caravan = 5 / 60.0;
        Assert.Equal(3 + ((1 - Math.Exp(-visitor * 2)) / visitor) + (Math.Exp(-visitor * 2) * (1 - Math.Exp(-(visitor + caravan) * 25)) / (visitor + caravan)), expected, 9);
        Assert.Contains(TradeArrivalRules.OrbitalNoComms, stock.Findings);
        Assert.Contains(TradeArrivalRules.SilverIncomeUnmodelled, stock.Findings);

        // Without the native read the stock is unread and a wait on it throws.
        var bare = ColonyFactsCoreTests.Snapshot();
        var unread = RimGovernor.Brain.Observation.StockArrivalReads.Of(catalog, new HashSet<string>(), new BillsSnapshot { Context = bare.Context, Benches = [] }, null, bare, [new Resource("MedicineHerbal")]);
        Assert.Equal("stock_arrival_trade_facts", unread.Unread);
    }

    [Fact]
    public void UnderRandyTheFactionArrivalDrawGivesAFiniteTradeWaitForABionicArm()
    {
        // #3909: Randy sends traders only through RandomMain's category draw; the production way gives a finite wait.
        var (expected, stock) = ArmWait(TradeCatalog(), "Randy");

        // From day 1 at 1 ÷ 1.35 a day: FactionArrival's 2.4 of the 3.5 + 1.1 + 2.4 not ThreatBig, a third caravans and a third
        // visitors at 0.75.
        var lambda = 1 / 1.35;
        var notThreatBig = 1 - TradeArrivalRules.ThreatBigFraction(1.4 / (1.4 + 7.0), lambda, 13);
        var rate = lambda * notThreatBig * (2.4 / 7.0) * (1 + 0.75) / 3;
        Assert.Equal(1 + ((1 - Math.Exp(-rate * 29)) / rate), expected, 9);
        Assert.True(expected < 30);
        Assert.DoesNotContain(stock.Findings, f => f.StartsWith(TradeArrivalRules.CompUnmodelled, StringComparison.Ordinal));
    }

    [Fact]
    public void AFreshColonyWithoutAMedicineBenchForecastsTheMedicineItsTradersSell()
    {
        // The production way (#3822, #3907): the decoded comps and a caravan kind selling 3 to 5 herbal at 10 × 1.4 = 14 silver, the census as
        // the read boundary fills it for three colonists with 1000 silver and no medicine, the routine trade's reserve review of it, and
        // the colony model over it.
        var stock = FreshStock(TradeCatalog(), "Cassandra", 1000, "Caravan_Outlander_Medic");
        Assert.Equal(700, stock.Spendable);
        var items = ItemFactsFixtureRules.CoreItemFacts();
        var review = MedicalRules.ReviewMedicalReserve(new MedicalReserveObservation
        {
            Colonists = 3, Catalog = items, Items = Known<EquatableArray<MedicineStackRow>>([]), Resources = Known<EquatableArray<Amount>>([]),
        }, false, MedicalReservePolicy.Default());
        Assert.Equal((true, 9L), (review.Active, review.Replenish.Value));

        // The gap of 9 at the cheapest kind's 18 is claimed first: 162 of the 700.
        var claimed = stock.WithMedicineClaim(items, review, 3, MedicalReservePolicy.Default());
        var trade = claimed.MedicineTrade!;
        Assert.Equal((new Resource("MedicineHerbal"), 9.0, 38.0), (trade.Kind, trade.Claimed, trade.Units));
        // Visitors (4 a 60-day year, trading with 0.75) from day 3, caravans (5 a year) from day 5, each visit selling 4 on average.
        Assert.Equal([new MedicineTradeSource(3, 4 / 60.0 * 0.75 * 4), new MedicineTradeSource(5, 5 / 60.0 * 4)], trade.Sources);

        // The model buys from day 3 at 0.2 a day and from day 5 at 0.2 + 1/3: 0.4 by day 5, the 9 of the target on day 21.125.
        var input = MedicineValueTests.Colony(6, 0);
        var run = ColonyModel.Run(input, [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())], MedicineValueTests.Policy(0) with { MedicineTrade = trade });
        var stop = run.Events.First(e => e is { Kind: ModelEventKind.MedicineTrade, Id: "stop" });
        Assert.Equal(21.125, stop.Day, 9);
        Assert.Equal(9, stop.Levels[(int)ModelStock.Medicine], 9);
        Assert.DoesNotContain(MedicineTrade.LaterBuysFinding, run.Findings);
    }
}
