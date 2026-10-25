using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodStorageTotalTests
{
    [Theory]
    [InlineData(nameof(O.ColonyFactsSnapshot.Cooking))]
    [InlineData(nameof(O.ColonyFactsSnapshot.Butchering))]
    [InlineData(nameof(O.ColonyFactsSnapshot.FoodSupply))]
    public void EveryColonyFrameRequiresItsFoodCensuses(string member)
    {
        var json = JsonNode.Parse(WireJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        Assert.True(json.Remove(JsonNamingPolicy.CamelCase.ConvertName(member)));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<O.ColonyFactsSnapshot>(json.ToJsonString()));
    }

    [Fact]
    public void MissingFoodSupplyFailsTheDecoder()
    {
        Assert.Throws<BridgeContractException>(() => FoodSupplyReads.DecodeFoodSupply(null!, default, null));
    }

    [Fact]
    public void NoEatersHasNoMealTierAndMeasuredRunwayMustBeValid()
    {
        Assert.Equal(MealTier.None, MealRules.ReviewMealTier(new() { RawRunwayDays = new FoodRunway.NoEaters() }, []).Tier);
        Assert.Throws<MealTierFactsException>(() => MealRules.ReviewMealTier(new()
        {
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
            CoolerAvailable = true,
            Cells = [],
            Blackout = false,
        };
        var policy = FoodStorageUpkeepRules.DefaultFoodStoragePolicy();
        Assert.Throws<RequiredFactException>(() => RefrigerationRules.SelectRefrigerationMethod(review, observation, policy, false));
        var cooler = new RefrigerationCooler
        {
            Id = "cooler", Position = new(1, 1), Rotation = Rotation.North,
            Target = -5, Connected = true, PowerOn = true,
        };
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
