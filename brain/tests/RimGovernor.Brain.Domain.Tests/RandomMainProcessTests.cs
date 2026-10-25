using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RandomMainProcessTests
{
    private static readonly IReadOnlyList<(double, double)> Population = [(0, 0)];
    private const string Big = "ThreatBig";
    private const string Trader = "FactionArrival";
    private static readonly IReadOnlyDictionary<(int, string), double> Qualifies = new Dictionary<(int, string), double> { [(1, "Trader")] = 1 };

    [Fact]
    public void BlockedNonTraderWeightIsRedistributedThroughCategoryRedraw()
    {
        var chances = IncidentCategoryDraw.Draw([
            new("Blocked", 99, new Dictionary<string, double>()),
            new(Trader, 1, new Dictionary<string, double> { ["Trader"] = 1 }),
        ]);
        Assert.Equal(1, chances["Trader"], 12);
    }

    [Fact]
    public void FailedForcedThreatRedrawsTheTraderWithoutResettingTheThreatClock()
    {
        var facts = Facts(0, -1, 1) with
        {
            Categories = [Category(Big, 1, true, new(0.25, [new("Raid", 1, false)]), new(0.75, [])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)]))],
        };
        var process = new RandomMainProcess(facts, Population);
        Assert.Equal(Math.Pow(0.25, 4), process.NotArrived(0, 4000.0 / 60000, Qualifies), 12);
    }

    [Fact]
    public void AnotherCompsScheduledThreatResetsTheSameClockBeforeRandomMain()
    {
        var facts = Facts(4000, -10000, 1) with
        {
            Categories = [Category(Big, 0, true, new UsableIncidentSet(1, [new("Raid", 1, false)])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)]))],
        };
        var before = new RandomMainProcess(facts, Population);
        Assert.Equal(1, before.NotArrived(0, 1000.0 / 60000, Qualifies));
        var reset = new RandomMainProcess(facts with { Clock = facts.Clock! with { Resets = [new(1000.0 / 60000, 0, 1) { Outcomes = [new("Raid", 1, true)] }] } }, Population);
        Assert.Equal(0, reset.NotArrived(0, 1000.0 / 60000, Qualifies));
    }

    [Fact]
    public void ACategoryIncidentBecomesUsableAtItsLaterEarliestDay()
    {
        var facts = Facts(60000, 0, 1) with
        {
            Categories = [Category(Big, 0, true, new UsableIncidentSet(1, [])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false) { EarliestDay = 3000.0 / 60000 }]))],
        };
        var process = new RandomMainProcess(facts, Population);
        Assert.Equal(1, process.NotArrived(0, 2000.0 / 60000, Qualifies));
        Assert.Equal(0, process.NotArrived(0, 3000.0 / 60000, Qualifies));
    }

    [Fact]
    public void NativeIntervalSurvivalIsNotTheExponentialOfTheMarginalArrivalRate()
    {
        var facts = Facts(60000, 0, 0.5);
        var process = new RandomMainProcess(facts, Population);
        Assert.Equal(0.75, process.NotArrived(0, 1000.0 / 60000, Qualifies), 12);
        Assert.NotEqual(Math.Exp(-0.25), process.NotArrived(0, 1000.0 / 60000, Qualifies));
    }

    [Fact]
    public void AWantStartingLaterInheritsTheClockDistributionFromBeforeTheWant()
    {
        var facts = Facts(1000, -10000, 1);
        var process = new RandomMainProcess(facts, Population);
        Assert.Equal(0.25, process.NotArrived(2000.0 / 60000, 1000.0 / 60000, Qualifies), 12);
    }

    [Fact]
    public void SpaceChecksUseTheSharedAnyIncidentCooldownIncludingLocalNonThreats()
    {
        var facts = Facts(60000, 0, 1) with
        {
            Categories = [Category(Big, 0, true, new UsableIncidentSet(1, [])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)]))],
        };
        facts = facts with { Clock = facts.Clock! with
        {
            RandomMain = [new(1, -1, 1, 60000, 2000)],
            Resets = [new(2000.0 / 60000, 0, 0.5) { Outcomes = [new("Disease", 1, false)] }],
        } };
        var process = new RandomMainProcess(facts, Population);
        Assert.Empty(process.Emissions(1, "Trader", 2000.0 / 60000));
        Assert.Equal(0.5, process.Emissions(1, "Trader", 2001.0 / 60000).Single().Chance, 12);
        Assert.Equal(0.5, process.NotArrived(0, 2000.0 / 60000, Qualifies), 12);
        Assert.Equal(0, process.NotArrived(0, 4000.0 / 60000, Qualifies), 12);
        // Disease and Trader are alternative branches of this interval, rather than independent marginals.
        var either = new Dictionary<(int, string), double> { [(0, "Disease")] = 1, [(1, "Trader")] = 1 };
        Assert.Equal(0, process.NotArrived(0, 2000.0 / 60000, either), 12);
    }

    [Fact]
    public void ALocalNonThreatPreservesTheForcedBigClockWhileDelayingItsNextAttempt()
    {
        var facts = Facts(4000, -10000, 1) with
        {
            Categories = [Category(Big, 0, true, new UsableIncidentSet(1, [new("Raid", 1, false)])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)]))],
        };
        facts = facts with { Clock = facts.Clock! with { RandomMain = [new(1, -1, 1, 4000, 2000)] } };
        Assert.Equal(0, new RandomMainProcess(facts, Population).NotArrived(0, 4000.0 / 60000, Qualifies));
        facts = facts with { Clock = facts.Clock! with { Resets = [new(2000.0 / 60000, 0, 1) { Outcomes = [new("Local", 1, false)] }] } };
        Assert.Equal(1, new RandomMainProcess(facts, Population).NotArrived(0, 4000.0 / 60000, Qualifies));
    }

    [Fact]
    public void FailedExecutionConsumesSelectionWithoutRedrawOrClockReset()
    {
        var facts = Facts(60000, 0, 1) with
        {
            Categories = [Category(Big, 0, true, new UsableIncidentSet(1, [])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false) { ExecutionChance = 0.25 }]))],
        };
        facts = facts with { Clock = facts.Clock! with { LastIncidentTick = -2000, RandomMain = [new(1, -1, 1, 60000, 2000)] } };
        var process = new RandomMainProcess(facts, Population);
        Assert.Equal(0.75 * 0.75, process.NotArrived(0, 2000.0 / 60000, Qualifies), 12);
        Assert.Equal([0.25, 0.75 * 0.25], process.Emissions(1, "Trader", 2001.0 / 60000).Select(e => e.Chance));
        var forced = facts with
        {
            Clock = facts.Clock! with { RandomMain = [new(1, -1, 1, 0, 0)] },
            Categories = [Category(Big, 0, true, new UsableIncidentSet(1, [new("Raid", 1, false) { ExecutionChance = 0 }])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)]))],
        };
        Assert.Equal(1, new RandomMainProcess(forced, Population).NotArrived(0, 4000.0 / 60000, Qualifies));
    }

    [Fact]
    public void DifferingCompSpacingsKeepNativeOrderAndOneSharedClock()
    {
        var facts = Facts(60000, 0, 1) with
        {
            Clock = new(0, 0, 0, 0.2, [new(1, -1, 1, 60000, 2000), new(2, -1, 1, 60000, 3000)], []),
            Categories =
            [
                Category(Big, 0, true, new UsableIncidentSet(1, [])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)])),
                new(2, Big, [new UsableIncidentSet(1, [])]) { ThreatBig = true }, new(2, Trader, [new UsableIncidentSet(1, [new("OtherTrader", 1, false)])]) { Weight = 1 },
            ],
        };
        var process = new RandomMainProcess(facts, Population);
        Assert.Empty(process.Emissions(2, "OtherTrader", 0.1));
        Assert.Equal([2000.0 / 60000, 4000.0 / 60000], process.Emissions(1, "Trader", 0.1).Select(e => e.Day));
    }

    [Fact]
    public void LocalRenewalResetUsesItsOwnLastFireClock()
    {
        var resets = Enumerable.Range(1, 5).Select(n => new IncidentClockReset(n * 1000.0 / 60000, 0, 1)
        { Renewal = true, RenewalMinTicks = 2000, RenewalMaxTicks = 4000, Outcomes = [new("Haze", 1, false)] });
        var facts = new TradeVisitFacts
        {
            Clock = new(0, 0, 0, 0.1, [], EquatableArray.From(resets)) { RenewalIncident = "Haze", LastRenewalTick = 0 },
        };
        var process = new RandomMainProcess(facts, Population);
        Assert.Equal([2000.0 / 60000, 4000.0 / 60000], process.Emissions(0, "Haze", 0.1).Select(e => e.Day));
    }

    [Fact]
    public void AlternativeResourcesShareOneIncidentAndOneExclusiveGeneratorDraw()
    {
        var a = new Resource("A");
        var b = new Resource("B");
        var offer = new TraderOffer { UnitValue = 1, Draws = [new(2, 1, 1, 1, 1, 1, 0, 0, 1)] };
        var kind = new TraderKindStock { Offers = new Dictionary<Resource, TraderOffer> { [a] = offer, [b] = offer } };
        var facts = Facts(60000, 0, 0.5) with
        {
            Categories = [Category(Big, 0, true, new UsableIncidentSet(1, [])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)]))],
        };
        var program = new RandomMainProcess(facts, Population);
        TradeSource Source(Resource resource) => new(resource, 0, 0)
        { Timing = new IncidentTiming.Process(program, 1, "Trader"), PriceFactor = 1, Silver = 10, Kinds = [new(1, offer) { Stock = kind }] };
        var stock = new StockArrival { Trade = [Source(a), Source(b)] };
        var want = new StockNeed([a, b], 1);
        // Each visit picks exactly one of A/B. Either satisfies this want, so one roll has survivor 1/2.
        Assert.Equal(0.5, stock.NotArrived(want, 1000.0 / 60000), 12);
        Assert.Equal(0.25, stock.NotArrived(want, 2000.0 / 60000), 12);
        var withLocalReset = facts with { Clock = facts.Clock! with
        {
            LastIncidentTick = -2000, RandomMain = [new(1, -1, 0.5, 60000, 2000)],
            Resets = [new(1000.0 / 60000, 0, 1) { Outcomes = [new("Local", 1, false)] }],
        } };
        var blocked = new RandomMainProcess(withLocalReset, Population);
        stock = stock with { Trade = EquatableArray.From(stock.Trade.Select(s => s with { Timing = new IncidentTiming.Process(blocked, 1, "Trader") })) };
        Assert.Equal(1, stock.NotArrived(want, 2000.0 / 60000));
        Assert.Equal(0.5, stock.NotArrived(want, 3000.0 / 60000), 12);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void SpatialExecutionConditionControlsResetAndSupply(double succeeds, double survivor)
    {
        var facts = Facts(60000, 0, 1) with
        {
            Categories = [Category(Big, 0, true, new UsableIncidentSet(1, [])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false) { ExecutionChance = succeeds }]))],
        };
        var process = new RandomMainProcess(facts, Population);
        Assert.Equal(survivor, process.NotArrived(0, 1000.0 / 60000, Qualifies));
        Assert.Equal(succeeds, process.Emissions(1, "Trader", 1001.0 / 60000).Sum(e => e.Chance));
    }

    [Fact]
    public void SuppressedExecutionKeepsTheSelectedBigCategoryUntilItsNativeBoundary()
    {
        var facts = Facts(int.MaxValue, 0, 1) with
        {
            Categories =
            [
                Category(Big, 1, true, new UsableIncidentSet(1, [new("Raid", 1, false) { ExecuteAfterDay = 3000.0 / 60000 }])),
                Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)])),
            ],
        };
        var process = new RandomMainProcess(facts, Population);
        Assert.Equal(0.5, process.NotArrived(0, 1000.0 / 60000, Qualifies), 12);
        Assert.Empty(process.Emissions(1, "Raid", 3000.0 / 60000));
        Assert.Equal(0.5, process.Emissions(1, "Raid", 3001.0 / 60000).Single().Chance, 12);
    }

    [Fact]
    public void ClockAndCategoryFactsRoundTripThroughTheGeneratedJsonContract()
    {
        var facts = Facts(60000, 0, 0.5) with { Clock = new(0, 0, 0, 1, [new(1, 0, 0.5, 60000, 1000)],
            [new(0.5, 0, 0.25) { Outcomes = [new("Local", 1, false)] }]) };
        var json = System.Text.Json.JsonSerializer.Serialize(facts, typeof(TradeVisitFacts), RimGovernor.Brain.GameFacts.FactsJsonContext.Default);
        var read = (TradeVisitFacts)System.Text.Json.JsonSerializer.Deserialize(json, typeof(TradeVisitFacts), RimGovernor.Brain.GameFacts.FactsJsonContext.Default)!;
        Assert.Equal(facts, read);
    }

    private static TradeVisitFacts Facts(int forceTicks, int lastBig, double roll) => new()
    {
        Clock = new(0, lastBig, 0, 0.2, [new(1, -1, roll, forceTicks, 0)], []),
        Categories = [Category(Big, 1, true, new UsableIncidentSet(1, [new("Raid", 1, false)])), Category(Trader, 1, false, new UsableIncidentSet(1, [new("Trader", 1, false)]))],
    };

    private static IncidentCategoryRead Category(string name, double weight, bool big, params UsableIncidentSet[] outcomes) =>
        new(1, name, EquatableArray.From(outcomes)) { Weight = weight, ThreatBig = big };
}
