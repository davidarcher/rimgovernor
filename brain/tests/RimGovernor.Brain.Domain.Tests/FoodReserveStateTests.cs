using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The food reserve is a named state, not an unread fact: no consumer is a state of its own, and a reviewed reserve carries its review.</summary>
public sealed class FoodReserveStateTests
{
    private static FoodSupply Supply(params FoodConsumer[] consumers) => new() { Larder = new(), Consumers = consumers };

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
        var supply = Supply(new FoodConsumer { Id = new PawnId("c1"), NutritionPerDay = 1.6 });
        Assert.Throws<FoodFactsException>(() => FoodReserveRules.ReviewFoodReserve(supply, null, 0, 1, new FoodDeliveries.Unconfirmed()));
    }
}
