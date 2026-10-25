using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class BabyCareExposureTests
{
    private static BabyRead Baby(BabyCareRead care) => new()
    {
        Care = care, CribMood = new(0, 0, 0), Pawn = "baby", Fits = [], Play = null, InNursery = false,
        Hearers = [], SleepHearers = [], SleepWithoutNursery = new("", false, true, .00003, 0, 21),
    };

    private static ForecastCoupledContext Context(BabyRead baby) => ForecastValueTests.Context(ForecastValueTests.Pawn()) with
    {
        Needs = NeedsFixtures.Empty with
        {
            Pawns = [ForecastValueTests.Pawn(), NeedsFixtures.Pawn("baby")],
            Babies = NeedsFixtures.NoBabies with { Babies = [baby] },
        },
    };

    [Fact]
    public void FeedingBoundsIncludeInitialDeficitAndLaterHungerRatherThanWholeDayCare()
    {
        const double horizon = 60000;
        var share = ForecastBabyCare.ServiceShare(.5, .00001, 1d / 5000, horizon);
        Assert.Equal((.5 + .6) * 5000 / horizon, share, 12);
        Assert.True(share < .1);
        Assert.Equal(1, ForecastBabyCare.ServiceShare(0, .00001, 1d / 5000, 100));
        Assert.Equal(.05, ForecastBabyCare.ServiceShare(1, .00001, 1d / 5000, horizon), 12);
    }

    [Fact]
    public void AlternativeFeedersRetainOneDemandAndNativePositionBounds()
    {
        var locations = new ServicePositionRead.Possible([new(1, 1), new(6, 4)]);
        var care = new BabyCareRead
        {
            Active = null, Play = null, Food = new(.5, .00001, 1d / 5000),
            Opportunities = [new("one", BabyCareKind.Breastfeed, 2, locations, locations, ActivitySocialMode.Normal),
                new("two", BabyCareKind.BottleFeed, 2, locations, locations, ActivitySocialMode.Normal)],
        };
        var rows = ForecastBabyCare.Of(Context(Baby(care)), false, 60000, new HashSet<string>());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("food/baby", row.Demand);
            Assert.Equal(0, row.LowerShare);
            Assert.Equal(ForecastBabyCare.ServiceShare(.5, .00001, 1d / 5000, 60000), row.UpperShare);
            Assert.Same(locations, row.BabyPosition);
        });
    }

    [Fact]
    public void CarriedBabySnapshotRetainsRawPickupCellAndObservedCarerMode()
    {
        var care = new BabyCareRead
        {
            Active = new("pawn", BabyCareKind.Carry, new(1, 2), new(8, 9), new ServicePositionRead.Possible([new(8, 9), new(9, 9)]), ActivitySocialMode.Quiet),
            Food = null, Play = null, Opportunities = [],
        };
        var rows = ForecastBabyCare.Of(Context(Baby(care)), true, 60000, new HashSet<string>());
        var row = rows[0];
        Assert.Equal(new ServicePositionRead.Located(new(1, 2)), row.BabyPosition);
        Assert.Equal(new ServicePositionRead.Located(new(8, 9)), row.CarerPosition);
        Assert.Equal(new ForecastSocialService.Verified(ActivitySocialMode.Quiet), row.Social);
        Assert.Equal(1, row.End);
        Assert.Equal(1, row.LowerShare);
        Assert.Equal(1, row.UpperShare);
        var continuation = rows[1];
        Assert.Equal("carry/baby", continuation.Demand);
        Assert.Equal(care.Active!.FuturePositions, continuation.BabyPosition);
        Assert.Equal(0, continuation.LowerShare);
        Assert.Equal(1, continuation.UpperShare);
    }

    [Fact]
    public void LactationRechargeAdmitsFutureServiceButSupplyCapsItsDuration()
    {
        var locations = new ServicePositionRead.Located(new(1, 1));
        var care = new BabyCareRead
        {
            Active = null, Play = null, Food = new(.5, .00001, 1d / 5000),
            Opportunities = [new("pawn", BabyCareKind.Breastfeed, 0, locations, locations, ActivitySocialMode.Normal)
                { FoodRefillPerTick = .000001 }],
        };
        var row = Assert.Single(ForecastBabyCare.Of(Context(Baby(care)), false, 60000, new HashSet<string>()));
        Assert.Equal(.000001 * 5000, row.UpperShare, 12);
    }

    [Fact]
    public void NoNativeFoodSupplyDoesNotInventAFeedingService()
    {
        var care = new BabyCareRead
        {
            Active = null, Play = null, Food = new(.5, .00001, 1d / 5000),
            Opportunities = [new("pawn", BabyCareKind.Breastfeed, 0,
                new ServicePositionRead.Located(new(1, 1)), new ServicePositionRead.Located(new(1, 1)), ActivitySocialMode.Normal)],
        };
        Assert.Empty(ForecastBabyCare.Of(Context(Baby(care)), false, 60000, new HashSet<string>()));
    }

    [Theory]
    [InlineData(-.1, .00001, .0002)]
    [InlineData(.5, -.00001, .0002)]
    [InlineData(.5, .0002, .0002)]
    public void InvalidNativeRatesFailTheRequiredRead(double level, double fall, double gain) =>
        Assert.Throws<RequiredFactException>(() => ForecastBabyCare.ServiceShare(level, fall, gain, 60000));
}
