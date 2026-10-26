using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodStorageTotalTests
{
    [Theory]
    [InlineData(nameof(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot.ProductionBenches))]
    [InlineData(nameof(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot.FoodSupply))]
    public void EveryColonyFrameRequiresItsFoodCensuses(string member)
    {
        var json = JsonNode.Parse(OrdersJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        Assert.True(json.Remove(JsonNamingPolicy.CamelCase.ConvertName(member)));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot>(json.ToJsonString()));
    }

    [Fact]
    public void MissingFoodSupplyFailsThePolicyView()
    {
        Assert.Throws<ArgumentNullException>(() => FoodForecastRules.FromCensus(null!));
    }

    [Fact]
    public void NoEatersHasNoMealTierAndMeasuredRunwayMustBeValid()
    {
        Assert.Equal(MealTier.None, MealRules.ReviewMealTier(new() { Environment = ControlledEnvironmentTests.NoEnvironment, RawRunwayDays = new FoodRunway.NoEaters() }, []).Tier);
        Assert.Throws<MealTierFactsException>(() => MealRules.ReviewMealTier(new()
        {
            Environment = ControlledEnvironmentTests.NoEnvironment,
            RawRunwayDays = new FoodRunway.Days(double.NaN),
            Plan = new(),
            MinDays = 1,
            TargetDays = 3,
        }, []));
    }

    [Fact]
    public void NoLayoutOrUnavailableCoolersOweNoPlannedCoolingRooms()
    {
        Assert.Empty(TombRules.WarmCoolingRooms(true, null, RoomTestFacts.NoRooms));
        Assert.Empty(TombRules.WarmCoolingRooms(false, new(), RoomTestFacts.NoRooms));
        var review = new RefrigerationReview().WithWarmRooms(["warm-room"]);
        Assert.True(review.Active);
        Assert.Equal("warm-room", Assert.Single(review.Rooms));
    }

    [Fact]
    public void RefrigerationRequiresTheRoomAndTheCoolersHotSide()
    {
        var review = new RefrigerationReview { Active = true, Rooms = ["missing-room"] };
        var observation = new RefrigerationObservation
        {
            Rooms = RoomTestFacts.NoRooms,
            Coolers = [],
            Candidates = [], Definitions = [], Builders = [],
            Cells = [],
            Blackout = false,
        };
        var policy = FoodStorageUpkeepRules.DefaultFoodStoragePolicy();
        Assert.Throws<RequiredFactException>(() => RefrigerationRules.SelectRefrigerationMethod(review, observation, policy, false));
        var native = PowerBuildingFixtures.Trader("cooler", "Cooler", new(1, 1), -200, -200, true);
        native = native with { Settings = native.Settings with { Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Temperature(new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = ConstructionFixtures.Context(), EntityId = native.Building.Id, Token = "cooler-control" }, -5) } };
        var cooler = new RefrigerationCooler { Observed = native };
        Assert.Same(native, cooler.Observed);
        Assert.Equal(new Cell(1, 1), cooler.Position);
        Assert.Equal(-5, cooler.Target);
        Assert.True(cooler.Connected);
        Assert.True(cooler.PowerOn);
        Assert.Throws<RequiredFactException>(() => new RefrigerationCooler { Observed = native with { Settings = native.Settings with { Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.None() } } }.Target);
        Assert.Throws<RequiredFactException>(() => RefrigerationRules.HeatRejectionBlocked(cooler,
            new Dictionary<string, RoomState>(), new Dictionary<Cell, string>(), new Dictionary<Cell, SiteCell>()));
        Assert.Equal(RefrigerationMethod.WaitBlackout,
            RefrigerationRules.SelectRefrigerationMethod(review, observation with { Blackout = true }, policy, false).Method);
    }

    [Fact]
    public void PendingReserveAccessRequiresTheRequestedStackRead()
    {
        var facts = new FoodStoresFacts
        {
            StockProtection = true,
            Reserve = new FoodReserveState.Reviewed(new FoodReserveReview { Hold = ["stack"] }),
        };
        var family = new FoodStoresFamily(FoodStorageUpkeepRules.DefaultFoodStoragePolicy(), true, false);
        var need = new Need { Id = FoodStoresRules.FoodStorageNeed, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet };
        Assert.Throws<RequiredFactException>(() => family.Propose([need], facts));
        Assert.Empty(family.Propose([need], facts with { ReserveTargets = new ReserveSupplyRead.Targets([]) }));
        var method = family.Propose([need], facts with
        {
            ReserveTargets = new ReserveSupplyRead.Targets([SupplyAllow.NewSupplyForbid("stack", "Pemmican", new(1, 1))]),
        });
        Assert.Single(method);
    }
}
