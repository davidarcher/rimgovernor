using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The food reserve is a named state, not an unread fact: no consumer is a state of its own, and a reviewed reserve carries its review.</summary>
public sealed class FoodReserveStateTests
{
    internal static FoodSupply Provision(double days = 10) => new()
    {
        Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] },
        Consumers = [new FoodConsumer { Id = new("a"), NutritionPerDay = 1, HumanMeatAcceptable = false }],
        Stocks = [Stock("meal", "MealSimple", days, false), Stock("pem-1", "Pemmican", 5, true)],
        Reserved = new HashSet<string>(StringComparer.Ordinal) { "pem-1" },
    };

    private static FoodStock Stock(string id, string definition, double nutrition, bool forbidden) => new()
    {
        Id = id, DefName = definition, Nutrition = nutrition, Count = 1, UnitMass = 1,
        Rot = new FoodRot.Keeps(), Place = new FoodPlace.Stands(true, ""), TemperatureC = 0, RotRate = 0,
        Form = new FoodForm.Item(), IsHumanMeat = false, RawMeat = false, IsHumanlike = false,
        Vegetable = false, RawClass = FoodIngredientClass.None, Holder = new(""), Eaters = [new("a")], Forbidden = forbidden,
    };

    internal static FoodPlan RecoveryPlan(FoodSupply supply, double lead) => FoodPlanRules.SupplyFoodPlan(new()
    {
        Demand = FoodForecastRules.ForecastFood(supply, null), ReserveDays = 5, MinDays = 3, TargetDays = 7,
        EmergencyDays = 3, Labor = 20000,
        Channels = [CandidateRules.FoodCandidate(CandidateKind.Crop, "field", new YieldSupply.Steady(2),
            CandidateState.Designated, new CandidateLead.Days(lead), new CandidateWork.PerDay(100))],
    });

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(12, true)]
    public void NativeCommittedRecoveryIsComparedWithAccessibleRunwayBeforeTheSeasonalMinimum(double lead, bool release)
    {
        var supply = Provision();
        var plan = RecoveryPlan(supply, lead);
        Assert.Equal(CandidateState.Designated, Assert.Single(plan.Portfolio).Channel.State);
        var review = Assert.IsType<FoodReserveState.Reviewed>(FoodReserveRules.ReviewFoodReserve(
            supply, null, 5, 3, FoodReserveRules.FoodDeliveryDays(plan))).Review;
        Assert.False(review.IsShort);
        Assert.Equal(release, review.Emergency);
        if (release) Assert.Equal("pem-1", Assert.Single(review.Release));
        else Assert.Empty(review.Release);
    }

    [Fact]
    public void UnsupportedUnselectedAlternativesDoNotEraseCommittedRecoveryEvidence()
    {
        var supply = Provision();
        var plan = RecoveryPlan(supply, 12);
        var unknown = new FoodPlanEntry { Channel = CandidateRules.FoodCandidate(CandidateKind.Crop, "alternative",
            new YieldSupply.Unpriced(), CandidateState.Closed, new CandidateLead.Unscheduled(), new CandidateWork.PerDay(100)),
            Decision = FoodPlanDecision.Hold };
        Assert.IsType<FoodDeliveries.Confirmed>(FoodReserveRules.FoodDeliveryDays(plan with { Unknown = [unknown] }));
        Assert.IsType<FoodDeliveries.Unconfirmed>(FoodReserveRules.FoodDeliveryDays(plan with
            { Unknown = [unknown with { Decision = FoodPlanDecision.Open }] }));
        Assert.IsType<FoodDeliveries.Unconfirmed>(FoodReserveRules.FoodDeliveryDays(plan with
            { Unknown = [unknown with { Channel = unknown.Channel with { State = CandidateState.Designated } }] }));
        var uncredited = Assert.Single(plan.Portfolio) with { Decision = FoodPlanDecision.Hold, DeliveredPerDay = 0 };
        Assert.Equal(12, Assert.Single(Assert.IsType<FoodDeliveries.Confirmed>(
            FoodReserveRules.FoodDeliveryDays(plan with { Portfolio = [uncredited] })).Days));
    }

    [Fact]
    public void MissingRecoveryEvidenceDoesNotInventFailureAndDoesNotBlockReleasingAnAlreadyShortReserve()
    {
        foreach (var deliveries in new FoodDeliveries[] { new FoodDeliveries.Unconfirmed(), new FoodDeliveries.Confirmed([]) })
        {
            var healthy = Assert.IsType<FoodReserveState.Reviewed>(FoodReserveRules.ReviewFoodReserve(Provision(), null, 5, 3, deliveries)).Review;
            Assert.False(healthy.Emergency);
            Assert.Empty(healthy.Release);
            var shortReview = Assert.IsType<FoodReserveState.Reviewed>(FoodReserveRules.ReviewFoodReserve(Provision(2), null, 5, 3, deliveries)).Review;
            Assert.True(shortReview.IsShort);
            Assert.Equal(["pem-1"], shortReview.Release);
        }
    }

    private static FoodSupply Supply(params FoodConsumer[] consumers) => new() { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] }, Consumers = consumers };

    [Fact]
    public void ANoConsumersReserveHasNoAccessPending()
    {
        Assert.False(FoodStoresRules.ReserveAccessPending(new FoodReserveState.NoConsumers()));
    }

    [Fact]
    public void ADisabledReserveHasNoAccessPending()
    {
        Assert.False(FoodStoresRules.ReserveAccessPending(new FoodReserveState.Disabled()));
    }

    [Fact]
    public void AReservePendingAccessIsOnlyAReviewedOneWithStacksToHoldOrRelease()
    {
        var held = new FoodReserveState.Reviewed(new FoodReserveReview { Hold = ["pem-1"] });
        Assert.True(FoodStoresRules.ReserveAccessPending(held));
        Assert.False(FoodStoresRules.ReserveAccessPending(new FoodReserveState.Reviewed(new FoodReserveReview())));
    }

    [Fact]
    public void BrokenReserveDaysStillThrowTheFactsException()
    {
        var supply = Supply(new FoodConsumer { Id = new PawnId("c1"), NutritionPerDay = 1.6,
        HumanMeatAcceptable = false });
        Assert.Throws<FoodFactsException>(() => FoodReserveRules.ReviewFoodReserve(supply, null, 0, 1, new FoodDeliveries.Unconfirmed()));
    }
}
