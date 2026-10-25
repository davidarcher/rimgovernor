using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3926: a resource runway reads whole, as unrated, idle or burning, and refuses negative inputs.</summary>
public sealed class ResourceRunwayRecordTests
{
    private static readonly Resource Steel = new("Steel");

    private static ResourceConsumption Consumption(long perWindow) => new()
    {
        WindowDays = 2,
        Recurring = new Dictionary<Resource, long> { [Steel] = perWindow },
    };

    [Xunit.Fact]
    public void ARunwayWithoutConsumptionIsUnrated()
    {
        var runway = ResourceRules.ForecastResourceRunway(Steel, 30, 4, 10, new Tick(5), null);
        Assert.IsType<RunwayRating.Unrated>(runway.Rating);
        Assert.False(runway.InDeficit);
        Assert.False(runway.Rating.TryRate(out _));
    }

    [Xunit.Fact]
    public void ARunwayThatBurnsNothingIsIdleAndShortBelowItsReserve()
    {
        Assert.Equal(new RunwayRating.Idle(false), ResourceRules.ForecastResourceRunway(Steel, 30, 0, 10, new Tick(5), Consumption(0)).Rating);
        var short_ = ResourceRules.ForecastResourceRunway(Steel, 5, 0, 10, new Tick(5), Consumption(0));
        Assert.Equal(new RunwayRating.Idle(true), short_.Rating);
        Assert.True(short_.InDeficit);
    }

    [Xunit.Fact]
    public void ABurningRunwayCarriesItsRateAndDaysTogether()
    {
        var runway = ResourceRules.ForecastResourceRunway(Steel, 30, 0, 10, new Tick(5), Consumption(20));
        var burning = Assert.IsType<RunwayRating.Burning>(runway.Rating);
        Assert.Equal(10, burning.PerDay);
        Assert.Equal(2, burning.StockDays);
        Assert.Equal(2, burning.DaysLeft);
        Assert.True(burning.Deficit);
        Assert.True(runway.Rating.TryRate(out var rate));
        Assert.Equal(10, rate);
    }

    [Xunit.Fact]
    public void ANegativeReserveStockOrOreThrows()
    {
        Assert.Throws<DomainException>(() => ResourceRules.ForecastResourceRunway(Steel, -1, 0, 0, new Tick(5), null));
        Assert.Throws<DomainException>(() => ResourceRules.ForecastResourceRunway(Steel, 0, -1, 0, new Tick(5), null));
        Assert.Throws<DomainException>(() => ResourceRules.ForecastResourceRunway(Steel, 0, 0, -1, new Tick(5), null));
    }
}
