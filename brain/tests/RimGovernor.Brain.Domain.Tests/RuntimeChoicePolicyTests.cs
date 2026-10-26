using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RuntimeChoicePolicyTests
{
    private static AcquisitionSource Source(string id, double yield, bool designated = false, bool plantation = false) => new()
    {
        Observed = new()
        {
            Source = new() { Id = id }, Token = id, Resource = "WoodLog", Yield = yield, Taken = false,
            Designation = designated ? new AcquisitionDesignation.Designated(new(0)) : new AcquisitionDesignation.Open(),
            Kind = new AcquisitionKind.Plant(false, 1, plantation),
        },
        Entity = new() { Id = id, DefName = "Tree", Label = id, Place = new EntityPlace.OnMap(1, new Cell(1, 1)) },
        Tree = true,
    };

    [Fact]
    public void OutstandingSupplyRoundsEachDesignationAndNeverCreditsGrownPlantationTwice()
    {
        var designated = Source("designated", 2.5, designated: true, plantation: true);
        var held = Source("held", 9);
        var first = Source("first", 4);
        var second = Source("second", 5);
        var result = ResourceRules.OutstandingSupply(new("WoodLog"), 20, [designated, held, first, second], new HashSet<Resource>(),
            new Dictionary<string, bool> { ["held"] = true }, 0, 10);
        Assert.Equal(7, result.Standing);
        Assert.Equal(10, result.Need);
        Assert.Equal([first, second], result.Open);
        Assert.Equal([4.0, 5.0], result.Priced.Select(row => row.Yield));
        Assert.Equal(0, ResourceRules.UncoveredSupply(-1));
    }

    [Fact]
    public void IngredientShortfallDistinguishesAbsentFactsAndSumsEveryMatchingContribution()
    {
        var key = new ResourceKey { Def = new("material") };
        var cost = new ResourceQuantity { Key = key, Count = 12 };
        Assert.Equal((12L, false), ResourceRules.IngredientShortfall(cost, []));
        Assert.Equal((5L, true), ResourceRules.IngredientShortfall(cost,
            [new() { Key = key, Count = 3 }, new() { Key = key, Count = 4 }]));
        Assert.Equal(14, ResourceRules.DerivedTarget(9, 5));
        Assert.Equal(-1, ResourceRules.OutstandingTarget(9, 10));
    }

    [Fact]
    public void StandingFieldsRoundTheirCombinedYieldAndHayOnlyCountsTheChosenCrop()
    {
        var crop = new CropChoice { Name = "crop", Harvests = new("fiber"), UnitsPerCell = .25, HarvestNutrition = .5 };
        var farms = new[] { ZoneFixtures.Growing("a", "crop", 1), ZoneFixtures.Growing("b", "crop", 1), ZoneFixtures.Growing("c", "other", 10) };
        Assert.Equal(1, FieldResourceRules.StandingFields([crop], farms, new("fiber")));
        Assert.Equal(0, FieldResourceRules.StandingFields([crop], farms, new("other")));
        Assert.Equal(2, AnimalFoodRules.OutstandingHay(3, crop, farms));
        Assert.Equal(0, AnimalFoodRules.OutstandingHay(.5, crop, farms));
    }

    [Fact]
    public void SeasonalFoodShortfallUsesAllConsumerRatesAndClampsOnlyTheKnownSurplus()
    {
        var forecast = new FoodForecast { UsableNutrition = 5, Consumers = [new() { NutritionPerDay = 1 }, new() { NutritionPerDay = 2 }] };
        Assert.Equal(7, FoodPlanRules.SeasonalShortfall(forecast, 4));
        Assert.Equal(0, FoodPlanRules.SeasonalShortfall(forecast, 1));
        Assert.Equal(6, FoodPlanRules.AcquisitionNutrition(new() { GapPerDay = 2, HorizonDays = 3 }));
        Assert.Equal(40000, FoodPlanRules.SupplyLabor(2));
    }

    [Fact]
    public void ConstructionHostUsesCommonStuffThenOrdinalTieAndRequiresFixedFacts()
    {
        StuffOption Option(string name, double value, bool common = true) => new()
        {
            Stuff = name, Value = value, Common = common,
            Stats = new Dictionary<string, double> { [StatDefNames.StatWorkToBuild] = name == "a" ? 7 : 9 },
            Costs = [new(new("material"), 2)],
        };
        var definition = new PlanningDefinition { Name = "station", StuffOptions = [Option("z", 1), Option("a", 1), Option("rare", 0, false)] };
        var facts = new ReviewFacts { Definitions = [definition] };
        Assert.Equal(7, ColonyPlanningRules.HostPiece(facts, "station")!.Value.Work);
        Assert.Null(ColonyPlanningRules.HostPiece(facts with { Definitions = [definition with { StuffOptions = [Option("rare", 0, false)] }] }, "station"));
        Assert.Throws<RequiredFactException>(() => ColonyPlanningRules.HostPiece(facts with { Definitions = [definition with { StuffOptions = [] }] }, "station"));
    }

    [Fact]
    public void PlannedTradeCapsOnlyTheOpenedPurchasesAndPreservesOtherNeedFacts()
    {
        var component = TradePolicyRules.ComponentResource;
        var need = new TradeNeed { ComponentShortfall = 10, MedicineReplenish = 7, Shortfall = [new(new("wood"), 8), new(new("missing"), 2)] };
        var capped = TradePolicyRules.RestrictToPlan(need, new Dictionary<Resource, long> { [new("wood")] = 3, [component] = 4 }, 2);
        Assert.Equal(3, Assert.Single(capped.Shortfall).Count);
        Assert.Equal(4, capped.ComponentShortfall);
        Assert.Equal(7, capped.MedicineReplenish);
        Assert.Equal(2, capped.Food.Nutrition);
        Assert.Equal(0, TradePolicyRules.MaxSpend(-1));
        var facts = new TradeSelectionFacts { ColonySilver = 10 };
        var selection = new TradeSelection { SilverReserve = 5 };
        Assert.True(TradePolicyRules.ReserveSatisfied(facts, selection, -5));
        Assert.False(TradePolicyRules.ReserveSatisfied(facts, selection, -6));
        Assert.True(TradePolicyRules.ReserveSatisfied(facts with { Favor = true }, selection, -6));
    }

    [Fact]
    public void ExportCompatibilityKeepsAnyBuyerAndUnsupportedGeneratorsOutrankNoTrade()
    {
        var result = TradePolicyRules.ExportBuyers(["none", "unsupported", "buys"], [new("wood"), new("cloth")],
            (kind, product) => kind == "unsupported" ? TradeCompatibility.UnsupportedGenerator
                : kind == "buys" && product.Value == "wood" ? TradeCompatibility.Trades : TradeCompatibility.DoesNotTrade,
            kind => kind == "buys" ? 50 : 20);
        Assert.Equal(50, result.CashCap);
        Assert.Equal(TradeCompatibility.Trades, result.Buys[new("wood")]);
        Assert.Equal(TradeCompatibility.UnsupportedGenerator, result.Buys[new("cloth")]);
        Assert.Equal(0, TradePolicyRules.ExportBuyers([], [new("wood")], (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException()).CashCap);
    }

    [Fact]
    public void DefenderRangeCapsTheCountButStillReadsEveryDefenderAndUsesTheShortestReach()
    {
        var weapons = Enumerable.Repeat(new WeaponDef { Ranged = true, Range = 30 }, 9).Append(new() { Ranged = true, Range = 12 }).Append(new() { Ranged = false, Range = 1 });
        Assert.Equal((8, 12.0), LayoutDefenseRules.DefenderRangeOf(weapons));
        Assert.Equal((0, 0.0), LayoutDefenseRules.DefenderRangeOf([]));
        IEnumerable<WeaponDef> Missing()
        {
            yield return new() { Ranged = true, Range = 30 };
            throw new RequiredFactException("last primary");
        }
        Assert.Throws<RequiredFactException>(() => LayoutDefenseRules.DefenderRangeOf(Missing()));
    }

    [Fact]
    public void NetworkGeometryIncludesTheSixCellBoundaryThenWalksOnlyConnectedConduits()
    {
        var building = PowerBuildingFixtures.Trader("b", "generator", new(10, 10), 10, 10, true);
        var topology = new PowerTopology { Buildings = [building], Conduits = [new(16, 10), new(17, 10), new(19, 10), new(10, 16), new(10, 17)] };
        Assert.Equal([new Cell(10, 16), new(10, 17), new(16, 10), new(17, 10)], LayoutDefenseRules.DefenseNetworkConduits(topology, "net"));
        Assert.Empty(LayoutDefenseRules.DefenseNetworkConduits(topology, "absent"));
    }

    [Fact]
    public void IedsReadExplosivesOnlyForReadyEligibleDefinitions()
    {
        var research = new ResearchFacts { Census = new() { Observed = new() { Context = BillWire.Context, Slots = [], Projects = [], Finished = [] } } };
        var blocked = new PlanningDefinition { Name = DefenseIEDs.Incendiary, Available = false };
        var ready = new PlanningDefinition { Name = DefenseIEDs.HighExplosive, Available = true, Explosive = new() { ExplosiveRadius = 4 }, FixedConstruction = new() { Costs = [] } };
        var facts = new ReviewFacts { Research = research, Definitions = [blocked, ready] };
        var (ieds, _) = DefenseIEDs.Options(facts, new Dictionary<Resource, long>(), new Dictionary<string, EquatableArray<Amount>>());
        Assert.Equal(new DefenseIED(ready.Name, 4, false), Assert.Single(ieds));
        Assert.Throws<RequiredFactException>(() => DefenseIEDs.Options(facts with { Definitions = [ready with { Explosive = null }] }, new Dictionary<Resource, long>(), new Dictionary<string, EquatableArray<Amount>>()));
        Assert.Equal(DefinitionReadiness.Unread, LayoutDefenseRules.DefinitionReadinessOf(facts, "absent"));
    }

    [Fact]
    public void StorageSpareUsesTheObservedCapacityAndDoesNotCreateNegativeHeadroom()
    {
        Assert.Equal(3, FoodStorageUpkeepRules.StorageSpare(10, 7));
        Assert.Equal(0, FoodStorageUpkeepRules.StorageSpare(10, 11));
    }

    [Fact]
    public void DrillSitesKeepNeededPositiveDepositsAndBreakEqualDistancesByCoordinates()
    {
        var layout = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Region = RegionRules.RectRegion(new Policy.Rectangle(10, 10, 3, 3)) }] };
        var center = layout.Center()!.Value;
        DeepResourceLump Lump(int x, int z, long count = 10, string resource = "ore") => new()
        { Definition = resource, Centre = new(center.X + x, center.Z + z), Count = count, CellCount = 1 };
        var a = Lump(-1, 0);
        var b = Lump(0, -1);
        var c = Lump(0, 1);
        var deep = DeepResourcesTests.NoDeepResources with
        {
            Lumps = [Lump(5, 5), c, b, a, Lump(0, 0, 0), Lump(0, 0, resource: "unneeded")],
            GroundScanners = [new MineralScanner { Definition = "scanner", Position = center, Powered = false, Working = false, TicksToNextFind = 0 }],
        };
        var research = new ResearchFacts { Census = new() { Observed = new() { Context = BillWire.Context, Slots = [], Projects = [], Finished = ["DeepDrilling", "scanner"] } } };
        var facts = new ReviewFacts { LayoutPlan = layout, Research = research, Definitions = [new() { Name = "drill", Available = true }] };
        var runways = new[] { new ResourceRunway { Resource = new("ore"), Target = 5, Stock = 0, Rating = new RunwayRating.Idle(true) } };
        var sites = DeepDrillRules.DeepDrillSites(facts, deep, runways, new() { DeepResources = [new("ore")] }, "drill", "scanner");
        Assert.Equal([a, b, c, deep.Lumps[0]], sites);
        Assert.Equal(5, DeepDrillRules.SiteDistance(new(3, 4), new(0, 0)));
        Assert.Equal(3, DeepDrillRules.AvailableYield(a, 3));
        Assert.Empty(DeepDrillRules.DeepDrillSites(facts with { LayoutPlan = null, Research = null! }, deep, runways, new(), "drill", "scanner"));
        Assert.Throws<RequiredFactException>(() => DeepDrillRules.DeepDrillSites(new(), deep, [], new(), "drill", "scanner"));
    }

    [Fact]
    public void StorageCandidateNamesDeduplicateSortAndRetainTheExistingSixteenLimit()
    {
        FoodStorageStock Stock(string name) => new(new FoodStock
        {
            Id = name, DefName = name, Nutrition = 1, Rot = new FoodRot.Rots((long)GameTime.TicksPerDay),
            Place = new FoodPlace.Stands(false, ""), TemperatureC = 20, RotRate = 1,
            Form = new FoodForm.Item(), IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = false,
            RawClass = FoodIngredientClass.None, Holder = new PawnId(""), Eaters = [], Count = 1, UnitMass = 0, Forbidden = false,
        });
        var stocks = Enumerable.Range(0, 20).Reverse().Select(i => Stock($"food-{i:D2}")).ToList();
        stocks.Add(stocks[0]);
        var names = FoodStorageUpkeepRules.FoodStorageDefNames(new() { Stocks = [.. stocks] }, FoodStorageUpkeepRules.DefaultFoodStoragePolicy());
        Assert.Equal(Enumerable.Range(0, 16).Select(i => $"food-{i:D2}"), names);
    }}