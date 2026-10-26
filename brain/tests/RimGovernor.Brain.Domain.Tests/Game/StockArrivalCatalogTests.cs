using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3868: the catalog's stock recipes, what makes a part an install recipe puts back, and the production way from the defs to a
// replacement's wait: a fresh colony without the bench waits on trade, which is named; one with it crafts the part at its rate.
public sealed class StockArrivalCatalogTests
{
    private static double PurchasedUnits(MedicineTrade trade, MedicineTradeSource source)
    {
        var visit = Assert.IsType<MedicineVisit>(source.Visit);
        return visit.Units(source.Kind, Math.Floor(trade.Silver / visit.Price));
    }

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
        PawnState[] crafters = [StockArrivalTestFacts.Crafter("Anna", 10, 1)];
        var stock = new Dictionary<Resource, long> { [new Resource("Plasteel")] = 100 };
        var replacement = new OrganReplacementRead("Arm", "InstallBionicArm")
        {
            PartThing = "BionicArm", PartOnMap = false, MedicineOnMap = true, MedicineCount = 2, EligibleDoctors = 1, SuccessChance = 0.95, DoctorSuccessChance = 0.95,
        };

        var fresh = StockArrivalTestFacts.Craft(recipes, [], crafters, stock) with { Medicines = [new Resource("MedicineHerbal")] };
        Assert.Empty(fresh.Sources);
        var (pick, reason, _) = SurgeryRules.RestorePick([(replacement, 1.0)], fresh, 30);
        Assert.Equal((null, SurgeryWantReason.PartShort), (pick, reason));

        // A fabrication bench at 1x: 8 hours × 2500 ticks ÷ 10 000 work, 2 arms a day; 100 plasteel pays for 6.
        var built = StockArrivalTestFacts.Craft(recipes, [StockArrivalTestFacts.Bench("FabricationBench", true, 1)], crafters, stock) with { Medicines = [new Resource("MedicineHerbal")] };
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

    private static DefinitionCatalog TradeCatalog(params TraderKindDef[] extra) => TradeCatalog(false, extra);

    private static DefinitionCatalog TradeCatalog(bool mixedMedicine, params TraderKindDef[] extra)
    {
        ThingDef arm = new() { defName = "BionicArm", tradeTags = ["TechHediff"] }, leg = new() { defName = "BionicLeg", tradeTags = ["TechHediff"] };
        ThingDef silver = new() { defName = "Silver" }, herbal = new() { defName = "MedicineHerbal", statBases = [new RimWorld.StatModifier { stat = Potency, value = 0.6f }] };
        ThingDef industrial = new() { defName = "MedicineIndustrial", statBases = [new RimWorld.StatModifier { stat = Potency, value = 1f }] };
        var medic = Medic(herbal);
        if (mixedMedicine)
        {
            var generator = new StockGenerator_MultiDef { countRange = new IntRange(4, 4) };
            GameMembers.MultiDefThings(generator) = [herbal, industrial];
            medic.stockGenerators = [generator];
        }
        var install = new RecipeDef
        {
            defName = "InstallBionicArm", workerClass = typeof(Recipe_InstallArtificialBodyPart), appliedOnFixedBodyParts = [Arm], ingredients = [Slot(arm, 1), Slot(herbal, 2)],
        };
        var shown = new DefStatTable.Value(1000f, true, null);
        var stats = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(arm, null)] = new([], null), [(leg, null)] = new([], null), [(herbal, null)] = new([], null), [(industrial, null)] = new([], null), [(silver, null)] = new([], null) },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
            {
                [(arm, null, CatalogStats.MarketValue)] = shown, [(leg, null, CatalogStats.MarketValue)] = shown,
                [(herbal, null, CatalogStats.MarketValue)] = new(10f, true, null),
                [(herbal, null, CatalogStats.MedicalQualityMax)] = new(.6f, true, null),
                [(industrial, null, CatalogStats.MarketValue)] = new(20f, true, null),
                [(industrial, null, CatalogStats.MedicalQualityMax)] = new(1f, true, null),
                [(silver, null, CatalogStats.MarketValue)] = new(1f, true, null),
            },
            new Dictionary<BuildableDef, IReadOnlyList<string>>(),
            new HashSet<CatalogStat>(CatalogStats.All));
        return new DefinitionCatalog(
            [
                silver, new TerrainDef { defName = "Soil" }, Potency, Arm, arm, leg, herbal, install, FactionArrival, OrbitalVisitor, Misc, ThreatBig, Caravan, Orbital, Visitors,
                Travelers, Cassandra(), Randy(), Outlander(), medic, .. (mixedMedicine ? new Def[] { industrial } : []), .. extra,
            ],
            stats, new CatalogDerived("Silver", "Wort", 10f));
    }

    [Fact]
    public void TraderCompatibilityNamesSupportedAbsentAndUnsupportedGenerators()
    {
        var catalog = TradeCatalog(new TraderKindDef { defName = "unsupported", stockGenerators = [null!] });
        var arm = new Resource("BionicArm");
        Assert.Equal(TradeCompatibility.Trades, catalog.TraderKindCanSupply("Caravan_Outlander_Exotic", new ResourceKey { Def = arm }));
        Assert.Equal(TradeCompatibility.DoesNotTrade, catalog.TraderKindCanSupply("Caravan_Outlander_Medic", new ResourceKey { Def = arm }));
        Assert.Equal(TradeCompatibility.UnsupportedGenerator, catalog.TraderKindCanSupply("unsupported", new ResourceKey { Def = arm }));
        Assert.Equal(0, catalog.TraderKindSilver("Caravan_Outlander_Medic"));
        Assert.Throws<DomainException>(() => catalog.TraderKindCanSupply("missing-kind", new ResourceKey { Def = arm }));
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
                new TradeVisitComp(TradeCompRate.FactionInteraction, 5) { Incident = "TraderCaravanArrival" },
                new TradeVisitComp(TradeCompRate.FactionInteraction, 3) { CompIndex = 1, Incident = "VisitorGroup" },
            ],
            cassandra.Comps.Where(c => c.Rate == TradeCompRate.FactionInteraction));
        var orbital = Assert.Single(cassandra.Comps, c => c.Rate == TradeCompRate.OnOffCycle);
        Assert.Equal(("OrbitalTraderArrival", 3), (orbital.Incident, orbital.CompIndex));
        Assert.Empty(cassandra.Unmodelled);

        // Randy's RandomMain draws both trader categories, ThreatBig apart.
        var random = Assert.Single(schedule.Storytellers["Randy"].Comps);
        Assert.Equal((TradeCompRate.Mtb, 1.0, 1 / 1.35), (random.Rate, random.MinDaysPassed, random.IncidentsPerDay));
        var draw = random.Draw!;
        Assert.Equal((TradeDrawRule.RandomMain, 0.2), (draw.Rule, draw.MinPopulationIntentFactor));
        Assert.Equal([new("Misc", false), new("OrbitalVisitor", true), new TradeCategoryWeight("FactionArrival", true)], draw.Categories);
        Assert.Equal(new TradeIncident("FactionArrival", TradeWorker.Travelers, 0), schedule.Incidents["TravelerGroup"]);
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
            new TraderIncidentRead("VisitorGroup") { Weight = 4, Sources = [new() { Faction = "OutlanderCivil", Visitors = VisitorAdultChanceTests.Adults, Kinds = [new TraderKindWeight(kind, 1)] }] },
        ],
    };

    private static TradeVisitFacts RandomMainFrame(TradeVisitFacts facts) => facts with
    {
        Clock = new(0, 0, 0, 60, [new(0, 1, 1000.0 / (1.35 * 60000), 13 * 60000, 0)], []),
        Categories =
        [
            new(0, "ThreatBig", [new(1, [new("Raid", 1, false)])]) { Weight = 1.4, ThreatBig = true },
            new(0, "Misc", [new(1, [new("Other", 1, false)])]) { Weight = 3.5 },
            new(0, "OrbitalVisitor", [new(1, [new("OrbitalTraderArrival", 3, false)])]) { Weight = 1.1 },
            new(0, "FactionArrival", [new(1, [new("TraderCaravanArrival", 4, false), new("VisitorGroup", 4, false), new("TravelerGroup", 4, false)])]) { Weight = 2.4 },
        ],
    };
    // Three colonists (the routine trade's reserve is 300 silver) and the silver they hold.
    private static StockArrival FreshStock(DefinitionCatalog catalog, string storyteller, long silver, string kind = "Caravan_Outlander_Exotic", TradeVisitFacts? facts = null, IReadOnlyList<Resource>? medicines = null, EquatableArray<IncidentCycleTimes>? cycles = null)
    {
        facts ??= Visits(kind) with { GroundSilver = silver };
        if (storyteller == "Randy" && facts.Clock is null) facts = RandomMainFrame(facts);
        var census = ColonyFactsCoreTests.Snapshot() with
        {
            Threat = new ThreatSection { Observed = ColonyFactsCoreTests.QuietThreat with { Storyteller = storyteller, Trade = facts, Cycles = cycles ?? [new(0, 60, [5, 20, 35, 50]), new(1, 60, [3, 10, 17, 24, 31, 38, 45, 52, 59]), new(3, 60, [4, 19, 34, 49])] } },
            Resources = [new Quantity { DefName = "Silver", Units = silver }],
            ColonistCount = 3,
        };
        var bills = new BillsSnapshot { Context = census.Context, Benches = [] };
        var pawns = new PawnSnapshot { Context = census.Context, Pawns = [] };
        return RimGovernor.Brain.Bridge.StockArrivalReads.Of(catalog, new HashSet<string>(), bills, pawns, census, medicines ?? [new Resource("MedicineHerbal")]);
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
    public void GroundArrivalsAndRequestsSpendTheNativeHomeStorageSilverInsteadOfTheMapCensus()
    {
        var facts = Visits() with { GroundSilver = 1570, Requests = [ForecastTradeRequestTests.Request(kind: "Caravan_Outlander_Exotic")], CommsConsole = true };
        var stock = FreshStock(TradeCatalog(), "Cassandra", 9000, facts: facts);
        Assert.All(stock.Trade, source => Assert.Equal(1270, source.Silver));
        Assert.All(Assert.Single(stock.Requests).Sources, source => Assert.Equal(1270, source.Silver));
        var empty = FreshStock(TradeCatalog(), "Cassandra", 9000, facts: facts with { GroundSilver = 300 });
        Assert.All(empty.Trade, source => Assert.Equal(0, source.Silver));
        Assert.All(Assert.Single(empty.Requests).Sources, source => Assert.Equal(0, source.Silver));
    }

    [Fact]
    public void AFreshColonysNativeFrameBuildsARequestPortfolioRootWithoutAnyUnmetDemand()
    {
        var native = ForecastTradeRequestTests.Request(kind: "Caravan_Outlander_Exotic");
        var stock = FreshStock(TradeCatalog(), "Cassandra", 2000, facts: Visits() with { CommsConsole = true, Requests = [native], GroundSilver = 2000 });
        var option = Assert.Single(stock.Requests);
        var arm = new Resource("BionicArm");
        var arrival = Assert.Single(option.Sources, s => s.Resource == arm);
        Assert.Equal(1700, arrival.Silver); // native census less the routine reserve for three colonists
        Assert.Equal(0, arrival.NotArrived(1, 2));
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0, sourceCount: 0, horizon: GameTime.TicksPerDay)
            with { StockArrival = stock };
        var candidates = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts));
        var root = Assert.Single(candidates.Alternatives, c => c.TradeRequest.Length != 0);
        Assert.Equal(option.Id, root.TradeRequest);
        Assert.Equal("trade/request/ally", root.ExclusiveOutcome);
        Assert.Empty(root.StockChanges); // the arrival is availability, never secured yield
        Assert.Same(stock, candidates.Context.Stock);
        Assert.DoesNotContain(stock.Trade, s => s.Once);
        var items = ItemFactsFixtureRules.CoreItemFacts();
        var reserve = MedicalRules.ReviewMedicalReserve(new MedicalReserveObservation
        {
            Catalog = items, Items = [], Census = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 3, Resources = [] },
        }, false, MedicalReservePolicy.Default());
        var claimed = stock.WithMedicineClaim(items, reserve, 3, MedicalReservePolicy.Default());
        Assert.Equal(0, Assert.Single(Assert.Single(claimed.Requests).Sources, s => s.Resource == arm).Kinds[0].MedicineSilverClaim);
    }

    [Fact]
    public void AFreshColonysExactSettlementPackBuildsAMissionRootWithoutUnmetDemand()
    {
        var catalog = TradeCatalog();
        var stock = FreshStock(catalog, "Cassandra", 2000);
        var settlement = ForecastSettlementMissionTests.Destination("Caravan_Outlander_Exotic");
        var pack = ForecastSettlementMissionTests.Pack(settlement.TraderKind);
        var option = Assert.IsType<ForecastSettlementMission>(ForecastSettlementMissions.Option(catalog.TraderStock(), settlement, pack, ["pawn"], 1700));
        Assert.True(CaravanDeparture.TryCreate([new("pawn")], [new("Silver", 1700)], settlement.Tile, out var departure));
        option = option with { Departure = departure, HomeTile = 1 };
        stock = stock with { Missions = [option] };
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0, sourceCount: 0, horizon: GameTime.TicksPerDay) with { StockArrival = stock };
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts));
        var root = Assert.Single(generated.Alternatives, c => c.TradeMission.Length != 0);
        Assert.Equal(option.Id, root.Id);
        Assert.Empty(root.StockChanges);
        Assert.Same(stock, generated.Context.Stock);
        var arrival = Assert.Single(option.Sources, s => s.Resource == new Resource("BionicArm"));
        Assert.Equal(3, arrival.From);
        Assert.Equal(.98, arrival.PriceFactor);
        Assert.Equal(1700, arrival.Silver);
        Assert.DoesNotContain(stock.Trade, s => s.Once);
        var items = ItemFactsFixtureRules.CoreItemFacts();
        var reserve = MedicalRules.ReviewMedicalReserve(new MedicalReserveObservation
        {
            Catalog = items, Items = [], Census = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 3, Resources = [] },
        }, false, MedicalReservePolicy.Default());
        var claimed = stock.WithMedicineClaim(items, reserve, 3, MedicalReservePolicy.Default());
        Assert.Equal(0, Assert.Single(Assert.Single(claimed.Missions).Sources, s => s.Resource == new Resource("BionicArm")).Kinds[0].MedicineSilverClaim);
    }

    [Fact]
    public void AFreshColonyWithoutAFabricationBenchHasAFiniteTradeWaitForABionicArm()
    {
        // The production way (#3822): the catalog's decoded comps and stock, the census as the read boundary fills it with the
        // native TradeVisitFacts, to the replacement the autopilot would book for a lost arm with no arm on the map and no bench.
        var catalog = TradeCatalog();
        var (expected, stock) = ArmWait(catalog, "Cassandra");

        // A visitor on day 3 trades with probability .75; the guaranteed caravan on day 5 ends the remaining wait.
        Assert.Equal(3.5, expected, 9);
        Assert.Contains(TradeArrivalRules.OrbitalNoComms, stock.Findings);
        Assert.Contains(TradeArrivalRules.SilverIncomeUnmodelled, stock.Findings);

        // An observed empty colony is valid; an omitted required trader census fails the read.
        var bare = ColonyFactsCoreTests.Snapshot();
        var empty = RimGovernor.Brain.Bridge.StockArrivalReads.Of(catalog, new HashSet<string>(), new BillsSnapshot { Context = bare.Context, Benches = [] }, null, bare, [new Resource("MedicineHerbal")]);
        Assert.Equal("", empty.Unread);
        var missing = bare with { Threat = bare.Threat with { Observed = bare.Threat.Observed with { Trade = null! } } };
        Assert.Equal("trader visit observation", Assert.Throws<RequiredFactException>(() =>
            RimGovernor.Brain.Bridge.StockArrivalReads.Of(catalog, new HashSet<string>(), new BillsSnapshot { Context = bare.Context, Benches = [] }, null, missing, [new Resource("MedicineHerbal")])).Fact);
    }

    [Fact]
    public void UnderRandyTheFactionArrivalDrawGivesAFiniteTradeWaitForABionicArm()
    {
        // #3909: Randy sends traders only through RandomMain's category draw; the production way gives a finite wait.
        var (expected, stock) = ArmWait(TradeCatalog(), "Randy");

        Assert.InRange(expected, 1, 30);
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
        Assert.All(stock.Trade, s => Assert.Equal(700, s.Silver));
        var items = ItemFactsFixtureRules.CoreItemFacts();
        var review = MedicalRules.ReviewMedicalReserve(new MedicalReserveObservation
        {
            Catalog = items, Items = [], Census = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 3, Resources = [] },
        }, false, MedicalReservePolicy.Default());
        Assert.Equal((true, 9L), (review.Active, review.Replenish));

        // Each visit pays its native price of 14 for the medicine it carries.
        var claimed = stock.WithMedicineClaim(items, review, 3, MedicalReservePolicy.Default());
        var trade = claimed.MedicineTrade!;
        Assert.Equal((9.0, 700.0), (trade.Claimed, trade.Silver));
        // Visitors (4 a 60-day year, trading with 0.75) from day 3, caravans (5 a year) from day 5, each visit selling 4 on average.
        Assert.Collection(trade.Sources,
            source =>
            {
                Assert.Equal((new Resource("MedicineHerbal"), 3d), (source.Kind, source.From));
                Assert.Equal(3, PurchasedUnits(trade, source), 12);
                Assert.Equal(14, source.Visit!.Price, 12);
            },
            source =>
            {
                Assert.Equal((new Resource("MedicineHerbal"), 5d), (source.Kind, source.From));
                Assert.Equal(4, PurchasedUnits(trade, source), 12);
                Assert.Equal(14, source.Visit!.Price, 12);
            });

        // Dated visits buy 3 on day 3, 4 on day 5, 1.5 on day 10, then the remaining .5 on day 20.
        var input = MedicineValueTests.Colony(6, 0);
        var run = ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())], MedicineValueTests.Policy(0) with { MedicineTrade = trade });
        var stop = run.Events.First(e => e is { Kind: ModelEventKind.MedicineTrade, Id: "arrival" } && e.Levels[(int)ModelStock.Medicine] == 9);
        Assert.Equal(20, stop.Day, 9);
        Assert.Equal(9, stop.Levels[(int)ModelStock.Medicine], 9);
        Assert.DoesNotContain(MedicineTrade.LaterBuysFinding, run.Findings);
    }

    [Fact]
    public void TheNativeCensusVisitDatesControlMedicinePurchasesWithinTheActualHorizon()
    {
        var catalog = TradeCatalog();
        var input = MedicineValueTests.Colony(6, 0);
        ColonyTrajectory Forecast(double visitor, double caravan)
        {
            EquatableArray<IncidentCycleTimes> rows = [new(0, 90, [caravan]), new(1, 90, [visitor]), new(3, 90, [])];
            var stock = FreshStock(catalog, "Cassandra", 1000, "Caravan_Outlander_Medic", cycles: rows);
            Assert.All(stock.Trade, source => Assert.Same(rows.Single(row => row.CompIndex == Assert.IsType<IncidentTiming.Scheduled>(source.Timing).Cycle.CompIndex),
                Assert.IsType<IncidentTiming.Scheduled>(source.Timing).Cycle));
            var trade = stock.WithMedicineClaim(ItemFactsFixtureRules.CoreItemFacts(), new() { Active = true, Replenish = 9 }, 3, MedicalReservePolicy.Default()).MedicineTrade;
            return ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())],
                MedicineValueTests.Policy() with { HorizonDays = 6, MedicineTrade = trade });
        }
        var early = Forecast(3, 5);
        var late = Forecast(8, 10);
        Assert.Equal(7, early.Events[^1].Medicine.Sum(), 9);
        Assert.Equal(0, late.Events[^1].Medicine.Sum(), 9);
        Assert.Equal((double[])[3d, 5d], early.Events.Where(e => e.Kind == ModelEventKind.MedicineTrade && e.Id == "arrival").Select(e => e.Day));
        Assert.DoesNotContain(late.Events, e => e.Kind == ModelEventKind.MedicineTrade && e.Id == "arrival");
        Assert.DoesNotContain(ColonyTrajectory.TruncatedFinding, early.Findings);
        Assert.DoesNotContain(ColonyTrajectory.TruncatedFinding, late.Findings);
    }

    [Fact]
    public void AFreshColonyForecastsEitherMedicineFromOneNativeMultiDefDrawAndPaysItsNativePrice()
    {
        Resource herbal = new("MedicineHerbal"), industrial = new("MedicineIndustrial");
        var stock = FreshStock(TradeCatalog(true), "Cassandra", 342, "Caravan_Outlander_Medic", medicines: [herbal, industrial]);
        var claimed = stock.WithMedicineClaim(ItemFactsFixtureRules.CoreItemFacts(), new() { Active = true, Replenish = 9 }, 3, MedicalReservePolicy.Default());
        var trade = Assert.IsType<MedicineTrade>(claimed.MedicineTrade);
        // 42 spendable: half the visits sell 3 herbal at 14; the other half sell 1 industrial at 28.
        Assert.Equal(0, trade.KindPerDayAt(herbal, 5));
        Assert.Equal(0, trade.KindPerDayAt(industrial, 5));
        Assert.Equal(1.125 + 1.5, trade.Sources.Where(source => source.Kind == herbal).Sum(source => PurchasedUnits(trade, source)), 9);
        Assert.Equal(.375 + .5, trade.Sources.Where(source => source.Kind == industrial).Sum(source => PurchasedUnits(trade, source)), 9);
        Assert.Equal(26.25 + 35, trade.Sources.Sum(source => PurchasedUnits(trade, source) * source.Visit!.Price), 9);
        var input = MedicineValueTests.Colony(6, 0);
        var run = ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())], MedicineValueTests.Policy() with { MedicineTrade = trade });
        Assert.Equal(2, run.Events[^1].Medicine.Sum(), 9);
        Assert.Contains(industrial, run.MedicineKinds);
        Assert.DoesNotContain(ColonyTrajectory.TruncatedFinding, run.Findings);
        var empty = stock with { Trade = EquatableArray.From(stock.Trade.Select(s => s with { Silver = 0 })) };
        Assert.Null(empty.WithMedicineClaim(ItemFactsFixtureRules.CoreItemFacts(), new() { Active = true, Replenish = 9 }, 3, MedicalReservePolicy.Default()).MedicineTrade);
    }

    [Fact]
    public void AnOrbitalTraderOfAFreshColonyIsPaidOnlyFromTheSilverWithinBeaconRangeAboveTheReserve()
    {
        // The production way (#3822, #3910): three colonists with 1000 silver, 400 of it within the powered beacon's range, a comms
        // console and the Medic kind everywhere. The routine trade's reserve is 300: the caravan and visitors see 700, the orbital cycle 100.
        var facts = Visits("Caravan_Outlander_Medic") with { CommsConsole = true, TradeBeacon = true, BeaconSilver = 400, GroundSilver = 1000 };
        var stock = FreshStock(TradeCatalog(), "Cassandra", 1000, facts: facts);
        var herbal = new Resource("MedicineHerbal");
        var orbital = stock.Trade.Single(s => s.Resource == herbal && s.From == 0);
        Assert.Equal(100, orbital.Silver);
        Assert.All(stock.Trade.Where(s => s.From > 0), s => Assert.Equal(700, s.Silver));
        // 100 silver pays 7 herbal at 14; the map's 700 pays all five a visit can carry.
        Assert.Equal(4, orbital.Kinds[0].Stock!.CheapestMedicineUnits(herbal, [herbal], 7), 9);

        // Silver within range that is all reserve pays nothing, whatever the map holds.
        var none = FreshStock(TradeCatalog(), "Cassandra", 1000, facts: facts with { BeaconSilver = 300 }).Trade.Single(s => s.Resource == herbal && s.From == 0);
        Assert.Equal(0, none.Silver);
        Assert.Equal(0, none.Supplies(1));
    }
}
