using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PresentTraderDepartureTests
{
    private static readonly Resource Arm = new("Arm");
    private static Trader Seller(bool orbital, int ticks = 100, long silver = 280) => new()
    {
        Id = orbital ? "ship" : "caravan", Orbital = orbital, Kind = "exotic", FactionId = "ally",
        CanTrade = true, Travelling = false, GoodsStacks = 1,
        Visit = new(new("pawn"), new(5, 6), ticks, [new(Arm.Value, 1, 80)]) { Silver = silver },
    };

    [Theory]
    [InlineData(false, 99, true)]
    [InlineData(false, 100, false)]
    [InlineData(true, 99, true)]
    [InlineData(true, 100, false)]
    public void NativeDeadlineBoundsTheProductionOpeningAcrossWalkAndCrewAvailability(bool orbital, int finish, bool fits)
    {
        var visit = Assert.Single(ForecastTradeVisits.Options([Seller(orbital)], 1));
        var work = Assert.Single(ForecastTradeExecution.Work(new() { Visits = [visit] }));
        var compared = ForecastActivities.CompareActivityPlans(new()
        {
            Deadline = 1000, Activities = [visit.Opening("seller")], Options = [new(work.Task, work.Pawn, 0, 0)],
            Pawns = [new(work.Pawn, "start", new ForecastCapacityWindows.Read([new(finish - 20, 1000) { StartLocation = "start" }]), 1)],
            Routes = [new(work.Pawn, "start", "seller", new RouteTicks.Reachable(20))],
        });
        Assert.Equal(fits, compared.Alternatives.Length > 0);
        var context = ForecastValueTests.Context(ForecastValueTests.Pawn()) with { Stock = new() { Visits = [visit] }, TradeWork = [work] };
        var allocation = new ForecastActivityAllocation(work.Task, work.Pawn, 0, finish, finish, finish, finish, 0, finish, 0);
        var selected = new ForecastActivityOutcomeComparison([visit.Id], new([allocation], finish, finish), [], 0, false);
        Assert.Equal(fits, ForecastTradeExecution.Ready(selected, context).Count == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyASelectedAffordablePurchaseBeforeDepartureCanServeALaterWant(bool orbital)
    {
        var seller = Seller(orbital);
        var visit = Assert.Single(ForecastTradeVisits.Options([seller], 1));
        StockNeed[] needs = [new([Arm], 1)];
        var unselected = new StockArrival { Visits = [visit] };
        Assert.Equal(1, unselected.At(1).NotArrived(needs[0], 1));
        var source = Assert.Single(visit.Sources);
        var purchased = new StockArrival { Trade = [source with { From = 99d / GameTime.TicksPerDay, Authorized = 1 }] };
        Assert.Single(ForecastTradeRequests.Authorize(purchased, needs));
        Assert.Equal(0, purchased.At(1).NotArrived(needs[0], 0));
        var missed = purchased with { Trade = [source with { From = 100d / GameTime.TicksPerDay }] };
        Assert.Empty(ForecastTradeRequests.Authorize(missed, needs));
        Assert.Equal(1, missed.At(1).NotArrived(needs[0], 1));
        var poor = Assert.Single(ForecastTradeVisits.Options([Seller(orbital, silver: 279)], 1));
        Assert.Empty(ForecastTradeRequests.Authorize(new() { Trade = poor.Sources }, needs));
        Assert.Equal(1, Assert.Single(poor.Sources).NotArrived(1, 1));
    }

    [Fact]
    public void EachSellerSpendsItsOwnNativeSilverAndADepartedSellerHasNoOpening()
    {
        var visits = ForecastTradeVisits.Options([Seller(false), Seller(true, silver: 279)], 1);
        Assert.Equal(0, Assert.Single(visits.Single(v => !v.Native.Orbital).Sources).NotArrived(1, 1));
        Assert.Equal(1, Assert.Single(visits.Single(v => v.Native.Orbital).Sources).NotArrived(1, 1));
        Assert.Empty(ForecastTradeVisits.Options([Seller(false, 0), Seller(true, 0)], 1));
        var reserved = Assert.Single(ForecastTradeVisits.Options([Seller(false, silver: 379)], 3));
        Assert.Equal(79, Assert.Single(reserved.Sources).Silver);
        Assert.Equal(0, Assert.Single(Assert.Single(ForecastTradeVisits.Options([Seller(false)], 0)).Sources).Silver);
    }
}
