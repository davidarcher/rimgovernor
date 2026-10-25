using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastTradeRequestTests
{
    internal static TradeRequestOption Request(string faction = "ally", string kind = "exotic") => new()
    {
        ConsoleId = "console", ConsoleCell = new(1, 1), FactionId = faction, TraderKind = kind, Kind = RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan,
        Goodwill = 80, GoodwillCost = 15, RelationAfterPayment = "Ally", LastRequestTick = -240000,
        CooldownRemainingTicks = 0, ArrivalMaxTicks = 120000, Eligible = true, NegotiatorIds = ["pawn"],
    };

    internal static TraderOffer Offer(double value = 1, int count = 1) => new()
    {
        UnitValue = value, Draws = [new StockDraw(1, 1, 1, 1, count, count, 0, 0, value)],
    };

    internal static TraderStockTable Catalog(Resource resource, double value = 1) => new()
    {
        Kinds = new Dictionary<string, TraderKindStock> { ["exotic"] = new() { Offers = new Dictionary<Resource, TraderOffer> { [resource] = Offer(value) } } },
    };

    [Fact]
    public void ARequestIsOneVisitAtTwoDaysAndItsStockIsNeverPromisedByANonselectedOption()
    {
        var arm = new Resource("arm");
        var option = Assert.Single(ForecastTradeRequests.Options(Catalog(arm), new TradeVisitFacts { Requests = [Request()] }, 100));
        var source = Assert.Single(option.Sources);
        Assert.True(source.Once);
        Assert.Equal(2, source.From);
        Assert.Equal(1, source.NotArrived(1, 1.999));
        Assert.Equal(0, source.NotArrived(1, 2));
        Assert.Equal(1, source.NotArrived(2, 1000)); // a single visit never repeats until it draws enough stock
        Assert.Equal(0, source.Rate(1));
        var stock = new StockArrival { Requests = [option] };
        Assert.Equal(10, stock.ExpectedWait([new([arm], 1)], 10));
        Assert.Equal(2, (stock with { Trade = option.Sources }).ExpectedWait([new([arm], 1)], 10));
    }

    [Fact]
    public void RequestOptionsRespectNativeEligibilityTheSharedCooldownAndThePaidRelation()
    {
        var catalog = Catalog(new("arm"));
        EquatableArray<ForecastTradeRequest> Options(TradeRequestOption r) => ForecastTradeRequests.Options(catalog, new TradeVisitFacts { Requests = [r] }, 100);
        Assert.Empty(Options(Request() with { Eligible = false }));
        Assert.Empty(Options(Request() with { CooldownRemainingTicks = 1 }));
        Assert.Empty(Options(Request() with { RelationAfterPayment = "Neutral", Goodwill = 15 }));
        Assert.Empty(Options(Request() with { NegotiatorIds = [] }));
        Assert.Empty(Options(Request() with { Kind = RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital }));
        var a = Assert.Single(Options(Request()));
        var b = Assert.Single(Options(Request(kind: "exotic") with { TraderKind = "exotic", GoodwillCost = 10 }));
        Assert.Equal(a.Commitment().ExclusiveOutcome, b.Commitment().ExclusiveOutcome);
        Assert.Equal(a.Id, a.Commitment().TradeRequest);
        Assert.Throws<RequiredFactException>(() => Options(Request(kind: "missing")));
    }

    [Fact]
    public void TheVisitHasOnlyItsGeneratorsProbabilityAndTodaysSpendableSilver()
    {
        var arm = new Resource("arm");
        var poor = Assert.Single(ForecastTradeRequests.Options(Catalog(arm, 100), new TradeVisitFacts { Requests = [Request()] }, 99));
        Assert.Equal(1, Assert.Single(poor.Sources).NotArrived(1, 100));
        var paid = Assert.Single(ForecastTradeRequests.Options(Catalog(arm, 100), new TradeVisitFacts { Requests = [Request()] }, 100));
        var random = Assert.Single(paid.Sources) with { Kinds = [new TradeKindChance(0.25, Offer(100))] };
        Assert.Equal(0.75, random.NotArrived(1, 100));
        Assert.Equal(2 + 8 * .75, (new StockArrival { Trade = [random] }).ExpectedWait([new([arm], 1)], 10), 10);
    }
}
