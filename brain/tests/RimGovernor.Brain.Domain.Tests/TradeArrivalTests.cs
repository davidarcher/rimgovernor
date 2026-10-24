using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3869: trader visits as a stock-arrival source. A stock generator picks a def with the chance its draw gives and makes
// StockGenerator.RandomCountOf of it; a visit's kind is drawn as the incident draws it; the visits come at the comp's rate from the
// day it starts, and a visit supplies the count when its kind's generators make it and today's silver pays the buy price.
public sealed class TradeArrivalTests
{
    private static readonly Resource Arm = new("BionicArm");

    [Fact]
    public void AGeneratorPicksTheDefByItsDrawOfDistinctCandidates()
    {
        // A Tag generator of 3 defs from 10 candidates; a 3~5 draw from 4 picks min(k, 4); a -4~5 draw picks max(0, k).
        Assert.Equal(0.3, new StockDraw(10, 1, 3, 3, 1, 1, 0, 0, 100).Chosen(), 12);
        Assert.Equal((3 + 4 + 4) / 3.0 / 4, new StockDraw(4, 1, 3, 5, 1, 1, 0, 0, 100).Chosen(), 12);
        Assert.Equal((1 + 2 + 3 + 4 + 5) / 10.0 / 10, new StockDraw(10, 1, -4, 5, 1, 1, 0, 0, 100).Chosen(), 12);
        // A MultiDef listing the def twice of 3; a SingleDef of it.
        Assert.Equal(2 / 3.0, new StockDraw(3, 2, 1, 1, 1, 1, 0, 0, 100).Chosen(), 12);
        Assert.Equal(1, new StockDraw(1, 1, 1, 1, 1, 1, 0, 0, 100).Chosen());
    }

    [Fact]
    public void TheCountIsRandomCountOfOverTheCountAndTotalPriceRanges()
    {
        // Count only: uniform over 2~4.
        Assert.Equal([0, 0, 1 / 3.0, 2 / 3.0], new StockDraw(1, 1, 1, 1, 2, 4, 0, 0, 100).Counts(3));
        // Price only: RoundToInt(price ÷ 100) over 0~1000 is at least 3 when the price is at least 250.
        var price = new StockDraw(1, 1, 1, 1, 0, 0, 0, 1000, 100).Counts(3);
        Assert.Equal(0.75, price[3], 12);
        Assert.Equal(0.05, price[0], 12);
        // Both: of 1~4 at 100 each, 1 and 2 lie in 100~250; up to 100 draws, the 101st kept whatever its price.
        var both = new StockDraw(1, 1, 1, 1, 1, 4, 100, 250, 100).Counts(4);
        var last = Math.Pow(0.5, 100) / 4;
        Assert.Equal(((1 - Math.Pow(0.5, 100)) / 2) + last, both[1], 15);
        Assert.Equal(last, both[4], 15);
        Assert.Equal(1, both.Sum(), 12);
        // Neither range set: nothing.
        Assert.Equal([1, 0], new StockDraw(1, 1, 1, 1, 0, 0, 0, 0, 100).Counts(1));
    }

    [Fact]
    public void AnOffersGeneratorsAddTheirCountsAndAVisitSuppliesWhatTheSilverPays()
    {
        // Two generators, each picking the arm half the time and making one: two arms a quarter of the time.
        var offer = new TraderOffer { UnitValue = 1400, Draws = [new StockDraw(2, 1, 1, 1, 1, 1, 0, 0, 1000), new StockDraw(2, 1, 1, 1, 1, 1, 0, 0, 1000)] };
        Assert.Equal(0.75, offer.AtLeast(1), 12);
        Assert.Equal(0.25, offer.AtLeast(2), 12);
        Assert.Equal(1, offer.AtLeast(0));

        // A negotiator's 10 % on a 1400 unit: 1260; 2000 silver pays one arm, not two.
        var source = new TradeSource(Arm, 0, 0.1) { Kinds = [new TradeKindChance(0.5, offer)], PriceFactor = 0.9, Silver = 2000 };
        Assert.Equal(1260, source.UnitPrice(1400));
        Assert.Equal(0.5 * 0.75, source.Supplies(1), 12);
        Assert.Equal(0, source.Supplies(2));
        Assert.Equal(Math.Exp(-0.1 * 0.375 * 10), source.NotArrived(1, 10), 12);

        // A kind whose offer's market value is unread throws once a wait reads it.
        var unread = source with { Kinds = [new TradeKindChance(1, new TraderOffer { Unread = "market_value:BionicArm" })] };
        Assert.Throws<RequiredFactException>(() => unread.Supplies(1));
    }

    private static readonly TraderKindStock Outlander = new()
    {
        Offers = new Dictionary<Resource, TraderOffer> { [Arm] = new() { UnitValue = 1400, Draws = [new StockDraw(10, 1, 3, 3, 1, 1, 0, 0, 1000)] } },
    };

    private static readonly TraderStockTable Stock = new()
    {
        Kinds = new Dictionary<string, TraderKindStock> { ["Outlander"] = Outlander, ["Bulk"] = new(), ["Royal"] = Outlander with { Favor = true }, ["Orbital"] = Outlander },
    };

    private static readonly StorytellerTrade Cassandra = new()
    {
        Comps =
        [
            new TradeVisitComp("TraderCaravanArrival", false, 5) { IncidentsPerDay = 5 / 60.0 },
            new TradeVisitComp("OrbitalTraderArrival", true, 0) { Cycle = new ThreatCycleComp(0, 7, 8, 1, 1, []) },
        ],
        Unmodelled = ["FactionInteraction:VisitorGroup"],
    };

    private static TradeVisitFacts Facts(double settled = 2) => new()
    {
        AllyFraction = 0.6, FullAllyFraction = -1, DaysSinceSettle = settled, CommsConsole = true, TradeBeacon = true, Negotiator = true,
        NegotiatorPriceImprovement = 0.1, PriceFactorLoss = 0,
        Incidents =
        [
            new TraderIncidentRead("OrbitalTraderArrival") { Sources = [new() { Kinds = [new TraderKindWeight("Orbital", 1)] }] },
            new TraderIncidentRead("TraderCaravanArrival")
            {
                Sources = [new() { Kinds = [new TraderKindWeight("Outlander", 1), new TraderKindWeight("Bulk", 3)] }, new() { Kinds = [new TraderKindWeight("Royal", 2)] }],
            },
        ],
    };

    [Fact]
    public void TheVisitsAreTheCompsRatesFromTheirStartWithTheKindDrawnAsTheIncidentDrawsIt()
    {
        var (sources, findings, unread) = TradeArrivalRules.Sources(Cassandra, Stock, Facts(), 0, 0, 5000);

        Assert.Empty(unread);
        // The caravans start in three days, the orbital cycle now.
        var caravan = sources.Single(s => s.From > 0);
        // 5 a 60-day year times the 0.6 ally fraction, from day 5, three days after now.
        Assert.Equal((Arm, 3.0, 5 / 60.0 * 0.6), (caravan.Resource, caravan.From, caravan.VisitsPerDay));
        // A faction drawn of two, then the outlander kind 1 of 4: an eighth; the royal kind trades for favor and is named.
        Assert.Equal([new TradeKindChance(0.125, Outlander.Offers[Arm])], caravan.Kinds);
        Assert.Equal(0.9, caravan.PriceFactor, 12);
        // The orbital cycle: one incident each 15 days.
        var orbital = sources.Single(s => s.From == 0);
        Assert.Equal((0.0, 1 / 15.0), (orbital.From, orbital.VisitsPerDay));
        Assert.Equal(1, Assert.Single(orbital.Kinds).Chance);
        Assert.Equal([TradeArrivalRules.CompUnmodelled + ":FactionInteraction:VisitorGroup", TradeArrivalRules.FavorUnmodelled + ":Royal", TradeArrivalRules.SilverIncomeUnmodelled],
            findings);
    }

    [Fact]
    public void ABlockedIncidentAnOrbitalWithoutCommsAndAColonyWithoutANegotiatorAreNamedZeros()
    {
        var noComms = TradeArrivalRules.Sources(Cassandra, Stock, Facts() with { CommsConsole = false }, 0, 0, 5000);
        Assert.DoesNotContain(noComms.Sources, s => s.From == 0);
        Assert.Contains(TradeArrivalRules.OrbitalNoComms, noComms.Findings);

        var blocked = Facts() with { Incidents = [Facts().Incidents[0], new TraderIncidentRead("TraderCaravanArrival") { Blocked = ["no_faction"] }] };
        var none = TradeArrivalRules.Sources(Cassandra, Stock, blocked, 0, 0, 5000);
        Assert.DoesNotContain(none.Sources, s => s.From > 0);
        Assert.Contains(TradeArrivalRules.Blocked + ":TraderCaravanArrival:no_faction", none.Findings);

        var alone = TradeArrivalRules.Sources(Cassandra, Stock, Facts() with { Negotiator = false }, 0, 0, 5000);
        Assert.Empty(alone.Sources);
        Assert.Contains(TradeArrivalRules.NoNegotiator, alone.Findings);

        // No ally: the faction interaction comp sends nothing; below a comp's min wealth neither.
        var lonely = TradeArrivalRules.Sources(Cassandra, Stock, Facts() with { AllyFraction = -1 }, 0, 0, 5000);
        Assert.DoesNotContain(lonely.Sources, s => s.From > 0);
        var rich = Cassandra with { Comps = [Cassandra.Comps[0] with { MinWealth = 20_000 }] };
        Assert.Empty(TradeArrivalRules.Sources(rich, Stock, Facts(), 0, 10_000, 5000).Sources);
    }

    [Fact]
    public void AnIncidentTheReadDoesNotCarryOrAKindTheCatalogDoesNotKnowLeavesTheTradeUnread()
    {
        Assert.Equal("trade_incident_unread:OrbitalTraderArrival", TradeArrivalRules.Sources(Cassandra, Stock, Facts() with { Incidents = [Facts().Incidents[1]] }, 0, 0, 5000).Unread);
        var stranger = Facts() with { Incidents = [Facts().Incidents[0], new TraderIncidentRead("TraderCaravanArrival") { Sources = [new() { Kinds = [new TraderKindWeight("Modded", 1)] }] }] };
        Assert.Equal("trader_kind_unknown:Modded", TradeArrivalRules.Sources(Cassandra, Stock, stranger, 0, 0, 5000).Unread);
    }
}
