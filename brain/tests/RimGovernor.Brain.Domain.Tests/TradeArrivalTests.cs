using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
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

    private static readonly TradeScheduleTable Schedule = new()
    {
        Incidents = new Dictionary<string, TradeIncident>
        {
            ["TraderCaravanArrival"] = new("FactionArrival", TradeWorker.Caravan, 0),
            ["VisitorGroup"] = new("FactionArrival", TradeWorker.Visitors, 0),
            ["TravelerGroup"] = new("FactionArrival", TradeWorker.Travelers, 0),
            ["OrbitalTraderArrival"] = new("OrbitalVisitor", TradeWorker.Orbital, 0),
        },
        ChildVisitorFactions = new HashSet<string> { "TribeCivil" },
        PopulationChanceByIntent = [(0, 0.05), (1, 0.3), (3, 0.45)],
    };

    private static readonly StorytellerTrade Cassandra = new()
    {
        Comps =
        [
            new TradeVisitComp(TradeCompRate.FactionInteraction, 5) { Incident = "TraderCaravanArrival", IncidentsPerDay = 5 / 60.0 },
            new TradeVisitComp(TradeCompRate.OnOffCycle, 0) { Incident = "OrbitalTraderArrival", Cycle = new ThreatCycleComp(0, 7, 8, 1, 1, []) },
        ],
        Unmodelled = ["StorytellerCompProperties_Modded"],
    };

    private static TradeVisitFacts Facts(double settled = 2) => new()
    {
        AllyFraction = 0.6, FullAllyFraction = -1, DaysSinceSettle = settled, CommsConsole = true, TradeBeacon = true, Negotiator = true,
        NegotiatorPriceImprovement = 0.1, PriceFactorLoss = 0, PopulationIntent = 1,
        Incidents =
        [
            new TraderIncidentRead("OrbitalTraderArrival") { Weight = 3, Sources = [new() { Kinds = [new TraderKindWeight("Orbital", 1)] }] },
            new TraderIncidentRead("TraderCaravanArrival")
            {
                Weight = 4, Sources = [new() { Kinds = [new TraderKindWeight("Outlander", 1), new TraderKindWeight("Bulk", 3)] }, new() { Kinds = [new TraderKindWeight("Royal", 2)] }],
            },
            new TraderIncidentRead("VisitorGroup") { Weight = 4, Sources = [new() { Faction = "OutlanderCivil", Kinds = [new TraderKindWeight("Outlander", 1)] }] },
            new TraderIncidentRead("TravelerGroup") { Weight = 4 },
        ],
    };

    [Fact]
    public void TheVisitsAreTheCompsRatesFromTheirStartWithTheKindDrawnAsTheIncidentDrawsIt()
    {
        var (sources, findings, unread) = TradeArrivalRules.Sources(Schedule, Cassandra, Stock, Facts(), new CycleNow(30, 0, 0, false), 0, 5000, 5000);

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
        Assert.Equal([TradeArrivalRules.CompUnmodelled + ":StorytellerCompProperties_Modded", TradeArrivalRules.FavorUnmodelled + ":Royal", TradeArrivalRules.SilverIncomeUnmodelled],
            findings);
    }

    [Fact]
    public void ABlockedIncidentAnOrbitalWithoutCommsAndAColonyWithoutANegotiatorAreNamedZeros()
    {
        var noComms = TradeArrivalRules.Sources(Schedule, Cassandra, Stock, Facts() with { CommsConsole = false }, new CycleNow(30, 0, 0, false), 0, 5000, 5000);
        Assert.DoesNotContain(noComms.Sources, s => s.From == 0);
        Assert.Contains(TradeArrivalRules.OrbitalNoComms, noComms.Findings);

        var blocked = Facts() with { Incidents = [.. Facts().Incidents.Select(i => i.Incident == "TraderCaravanArrival" ? i with { Blocked = ["no_faction"] } : i)] };
        var none = TradeArrivalRules.Sources(Schedule, Cassandra, Stock, blocked, new CycleNow(30, 0, 0, false), 0, 5000, 5000);
        Assert.DoesNotContain(none.Sources, s => s.From > 0);
        Assert.Contains(TradeArrivalRules.Blocked + ":TraderCaravanArrival:no_faction", none.Findings);

        var alone = TradeArrivalRules.Sources(Schedule, Cassandra, Stock, Facts() with { Negotiator = false }, new CycleNow(30, 0, 0, false), 0, 5000, 5000);
        Assert.Empty(alone.Sources);
        Assert.Contains(TradeArrivalRules.NoNegotiator, alone.Findings);

        // No ally: the faction interaction comp sends nothing; below a comp's min wealth neither, nor below its min danger.
        var lonely = TradeArrivalRules.Sources(Schedule, Cassandra, Stock, Facts() with { AllyFraction = -1 }, new CycleNow(30, 0, 0, false), 0, 5000, 5000);
        Assert.DoesNotContain(lonely.Sources, s => s.From > 0);
        var rich = Cassandra with { Comps = [Cassandra.Comps[0] with { MinWealth = 20_000 }] };
        Assert.Empty(TradeArrivalRules.Sources(Schedule, rich, Stock, Facts(), new CycleNow(30, 0, 0, false), 10_000, 5000, 5000).Sources);
        var danger = Cassandra with { Comps = [Cassandra.Comps[0] with { MinDanger = StoryDangerLevel.High }] };
        Assert.Empty(TradeArrivalRules.Sources(Schedule, danger, Stock, Facts() with { Danger = StoryDangerLevel.Low }, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Sources);
        Assert.Single(TradeArrivalRules.Sources(Schedule, danger, Stock, Facts() with { Danger = StoryDangerLevel.High }, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Sources);

        // An orbital incident with no kind that can spawn fires empty: named.
        var empty = Facts() with { Incidents = [.. Facts().Incidents.Select(i => i.Incident == "OrbitalTraderArrival" ? i with { Sources = [new() { Kinds = [] }] } : i)] };
        Assert.Contains(TradeArrivalRules.NoTraderKind + ":OrbitalTraderArrival", TradeArrivalRules.Sources(Schedule, Cassandra, Stock, empty, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Findings);
    }

    [Fact]
    public void AnIncidentTheReadDoesNotCarryOrAKindTheCatalogDoesNotKnowLeavesTheTradeUnread()
    {
        var withoutOrbital = Facts() with { Incidents = [.. Facts().Incidents.Where(i => i.Incident != "OrbitalTraderArrival")] };
        Assert.Equal("trade_incident_unread:OrbitalTraderArrival", TradeArrivalRules.Sources(Schedule, Cassandra, Stock, withoutOrbital, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Unread);
        var stranger = Facts() with
        {
            Incidents = [.. Facts().Incidents.Select(i => i.Incident == "TraderCaravanArrival" ? i with { Sources = [new() { Kinds = [new TraderKindWeight("Modded", 1)] }] } : i)],
        };
        Assert.Equal("trader_kind_unknown:Modded", TradeArrivalRules.Sources(Schedule, Cassandra, Stock, stranger, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Unread);
        var unknown = Cassandra with { Comps = [Cassandra.Comps[0] with { Incident = "ModdedArrival" }] };
        Assert.Equal("trade_incident_unknown:ModdedArrival", TradeArrivalRules.Sources(Schedule, unknown, Stock, Facts(), new CycleNow(30, 0, 0, false), 0, 5000, 5000).Unread);
    }

    // #3909: Randy's RandomMain comp, vanilla-shaped: mtbDays 1.35, ThreatBig forced after 13 days, the categories by weight.
    private static readonly StorytellerTrade Randy = new()
    {
        Comps =
        [
            new TradeVisitComp(TradeCompRate.Mtb, 1)
            {
                IncidentsPerDay = 1 / 1.35,
                Draw = new TradeCategoryDraw(TradeDrawRule.RandomMain)
                {
                    Categories =
                    [
                        new("Misc", 3.5, false), new("OrbitalVisitor", 1.1, true), new("FactionArrival", 2.4, true), new("ThreatSmall", 0.6, false),
                        new("ShipChunkDrop", 0.22, false),
                    ],
                    ThreatBigWeight = 1.4,
                    MaxThreatBigIntervalDays = 13,
                    MinPopulationIntentFactor = 0.2,
                },
            },
        ],
    };

    [Fact]
    public void ARandomMainDrawSendsEachTraderIncidentWithItsCategoryShareAfterThreatBigAndItsSelectChance()
    {
        var (sources, _, unread) = TradeArrivalRules.Sources(Schedule, Randy, Stock, Facts(), new CycleNow(30, 0, 0, false), 0, 5000, 5000);
        Assert.Empty(unread);

        // ThreatBig's long-run fraction: p = 1.4 ÷ 9.22 of the drawn events, one forced after 13 days without one.
        var lambda = 1 / 1.35;
        var p = 1.4 / 9.22;
        var threatBig = 1 / (1 + ((1 - p) * (1 - Math.Exp(-lambda * p * 13)) / p));
        Assert.Equal(threatBig, TradeArrivalRules.ThreatBigFraction(p, lambda, 13), 12);
        Assert.InRange(threatBig, p, 1);
        var factionArrival = lambda * (1 - threatBig) * 2.4 / 7.82;
        var orbitalVisitor = lambda * (1 - threatBig) * 1.1 / 7.82;

        // FactionArrival: caravans, visitors and travelers at 4 each, none changing the population: a third each; a visitor group
        // trades with 0.75. The comp starts on day 1, the colony settled 2 days ago.
        var caravan = sources.Single(s => s.Kinds.Any(k => k.Chance == 0.125));
        Assert.Equal(0, caravan.From);
        Assert.Equal(factionArrival / 3, caravan.VisitsPerDay, 12);
        var visitor = sources.Single(s => s.Kinds.Any(k => k.Chance == 0.75));
        Assert.Equal(factionArrival / 3, visitor.VisitsPerDay, 12);
        var orbital = sources.Single(s => s.Kinds.Any(k => k.Chance == 1));
        Assert.Equal(orbitalVisitor, orbital.VisitsPerDay, 12);

        // A blocked caravan leaves the category to visitors and travelers; a blocked category (no usable incident) is redrawn.
        var noCaravan = Facts() with { Incidents = [.. Facts().Incidents.Select(i => i.Incident == "TraderCaravanArrival" ? i with { Blocked = ["no_faction"] } : i)] };
        var visitorAlone = TradeArrivalRules.Sources(Schedule, Randy, Stock, noCaravan, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Sources.Single(s => s.Kinds.Any(k => k.Chance == 0.75));
        Assert.Equal(factionArrival / 2, visitorAlone.VisitsPerDay, 12);
        var noOrbital = Facts() with { Incidents = [.. Facts().Incidents.Select(i => i.Incident == "OrbitalTraderArrival" ? i with { Blocked = ["ships_full"] } : i)] };
        var others = 7.82 - 1.1;
        var redrawn = 1 - TradeArrivalRules.ThreatBigFraction(1.4 / (1.4 + others), lambda, 13);
        var caravanRedrawn = TradeArrivalRules.Sources(Schedule, Randy, Stock, noOrbital, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Sources.Single(s => s.Kinds.Any(k => k.Chance == 0.125));
        Assert.Equal(lambda * redrawn * 2.4 / others / 3, caravanRedrawn.VisitsPerDay, 12);
    }

    [Fact]
    public void TrySelectRandomIncidentSplitsByThePopulationIntentAndAChildVisitorFactionIsNamed()
    {
        // A visitor group that increases the population (medium: the intent's factor) against a caravan that does not: with the
        // curve's 0.3 at intent 1 the visitors, else the caravan (the travelers off).
        var schedule = Schedule with
        {
            Incidents = new Dictionary<string, TradeIncident>(Schedule.Incidents) { ["VisitorGroup"] = new("FactionArrival", TradeWorker.Visitors, 0) { ChangesPopulation = true, IntentOffset = 0 } },
        };
        var facts = Facts() with { Incidents = [.. Facts().Incidents.Where(i => i.Incident != "TravelerGroup")] };
        var withoutTravelers = schedule with { Incidents = schedule.Incidents.Where(i => i.Key != "TravelerGroup").ToDictionary() };
        var mtb = new StorytellerTrade
        {
            Comps = [new TradeVisitComp(TradeCompRate.Mtb, 0) { IncidentsPerDay = 0.1, Draw = new TradeCategoryDraw(TradeDrawRule.CategoryMtb) { Categories = [new("FactionArrival", 1, true)], MinPopulationIntentFactor = 0.05 } }],
        };
        var sources = TradeArrivalRules.Sources(withoutTravelers, mtb, Stock, facts, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Sources;
        Assert.Equal(0.1 * 0.7, sources.Single(s => s.Kinds.Any(k => k.Chance == 0.125)).VisitsPerDay, 12);
        Assert.Equal(0.1 * 0.3, sources.Single(s => s.Kinds.Any(k => k.Chance == 0.75)).VisitsPerDay, 12);

        // An on-off cycle by category draws by weight alone.
        var cycle = new StorytellerTrade
        {
            Comps = [new TradeVisitComp(TradeCompRate.OnOffCycle, 0) { Cycle = new ThreatCycleComp(0, 5, 5, 1, 1, []), Draw = new TradeCategoryDraw(TradeDrawRule.OnOffCycle) { Categories = [new("FactionArrival", 1, true)] } }],
        };
        var cycled = TradeArrivalRules.Sources(withoutTravelers, cycle, Stock, facts, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Sources;
        Assert.Equal(0.1 / 2, cycled.Single(s => s.Kinds.Any(k => k.Chance == 0.75)).VisitsPerDay, 12);
        // The same retained colony cycle inputs price trader cycles, including progress and the no-tree day substitution.
        var progressed = cycle with
        {
            Comps = [cycle.Comps[0] with
            {
                Cycle = cycle.Comps[0].Cycle! with { AcceptByProgressScore = [(0, 1), (10, 2)], OnDaysNoTreeConnectors = 2 },
            }],
        };
        var adjusted = TradeArrivalRules.Sources(withoutTravelers, progressed, Stock, facts, new CycleNow(30, 0, 5, true), 0, 5000, 5000).Sources;
        Assert.Equal(1.5 / 7 / 2, adjusted.Single(s => s.Kinds.Any(k => k.Chance == 0.75)).VisitsPerDay, 12);

        // A visitor faction whose Peaceful groups hold a child kind supplies nothing, named.
        var child = Facts() with { Incidents = [.. Facts().Incidents.Select(i => i.Incident == "VisitorGroup" ? i with { Sources = [new() { Faction = "TribeCivil", Kinds = [new TraderKindWeight("Outlander", 1)] }] } : i)] };
        var (childSources, childFindings, _) = TradeArrivalRules.Sources(Schedule, Randy, Stock, child, new CycleNow(30, 0, 0, false), 0, 5000, 5000);
        Assert.DoesNotContain(childSources, s => s.Kinds.Any(k => k.Chance == 0.75));
        Assert.Contains(TradeArrivalRules.VisitorChildUnmodelled + ":TribeCivil", childFindings);
    }

    [Fact]
    public void AnOrbitalTraderIsPaidFromTheSilverWithinBeaconRangeAndACaravanFromTheMaps()
    {
        // #3910: the orbital source prices a unit against the beacon's silver (1260 unaffordable from 100), the caravan against the map's.
        var (sources, _, unread) = TradeArrivalRules.Sources(Schedule, Cassandra, Stock, Facts(), new CycleNow(30, 0, 0, false), 0, 5000, 100);
        Assert.Empty(unread);
        var orbital = sources.Single(s => s.From == 0);
        var caravan = sources.Single(s => s.From > 0);
        Assert.Equal((100L, 5000L), (orbital.Silver, caravan.Silver));
        Assert.Equal(0, orbital.Supplies(1));
        Assert.Equal(0.125 * 0.3, caravan.Supplies(1), 12);
        Assert.Equal(0.3, TradeArrivalRules.Sources(Schedule, Cassandra, Stock, Facts(), new CycleNow(30, 0, 0, false), 0, 5000, 1260).Sources.Single(s => s.From == 0).Supplies(1), 12);
    }

    [Fact]
    public void AnOrbitalKindOfAFactionIsDrawnAsTheGameDrawsItsFactionAndItsShipsFaction()
    {
        // #3910: kind A has no faction (weight 1), kind B a faction def whose factions a colonist can trade with half the time (weight 3).
        // CanSpawn draws B's faction once: B is a candidate half the time. TryRandomElementByWeight then picks A at 1 ÷ 1 alone and 1 ÷ 4
        // beside B: 0.5 + 0.5 × 0.25. B is picked 3 ÷ 4 of the half it is a candidate, and its ship's own faction, drawn again, trades half
        // the time.
        var chances = TradeArrivalRules.OrbitalKindChances([new TraderKindWeight("A", 1), new TraderKindWeight("B", 3) { Qualifies = 0.5 }]);
        Assert.Equal(0.625, chances["A"], 12);
        Assert.Equal(0.5 * 0.75 * 0.5, chances["B"], 12);

        // Two uncertain kinds, A (1, ½) and B (3, ¼): A is picked alone or beside B when B passes its own draw.
        var both = TradeArrivalRules.OrbitalKindChances([new TraderKindWeight("A", 1) { Qualifies = 0.5 }, new TraderKindWeight("B", 3) { Qualifies = 0.25 }]);
        Assert.Equal(0.5 * 0.5 * ((0.75 * 1) + (0.25 / 4)), both["A"], 12);
        Assert.Equal(3 * 0.25 * 0.25 * ((0.5 / 3) + (0.5 / 4)), both["B"], 12);

        // A kind of a faction def with no faction, or no weight, is never drawn.
        Assert.Empty(TradeArrivalRules.OrbitalKindChances([new TraderKindWeight("A", 1) { Qualifies = 0 }, new TraderKindWeight("B", 0)]));

        // The visit's chance of a kind the colony trades with is the draw's: the source carries it.
        var facts = Facts() with
        {
            Incidents = [.. Facts().Incidents.Select(i => i.Incident == "OrbitalTraderArrival"
                ? i with { Sources = [new() { Kinds = [new TraderKindWeight("Orbital", 1), new TraderKindWeight("Outlander", 3) { Qualifies = 0.5 }] }] }
                : i)],
        };
        var orbital = TradeArrivalRules.Sources(Schedule, Cassandra, Stock, facts, new CycleNow(30, 0, 0, false), 0, 5000, 5000).Sources.Single(s => s.From == 0);
        Assert.Equal(0.625, orbital.Kinds.Single(k => k.Chance > 0.5).Chance, 12);
        Assert.Equal(0.1875, orbital.Kinds.Single(k => k.Chance < 0.5).Chance, 12);
    }
}
