using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3812: a planned pen is credited with the roaming losses it stops. Room 1 (PlannedRoomCreditTests' room, serving from day
/// three of a ten-day horizon) is a cows' pen; each cow that can reach the map edge walks off it once per RoamMtbDays on
/// average, losing its MarketValue, so the pen gains each cow's value in capability-days over its RoamMtbDays a day.
/// </summary>
public sealed class PenCreditTests
{
    private const double Horizon = 10, Served = 3, MtbDays = 2, CowValue = 300;

    /// <summary>A cow's value in capability-days: its market value at the ledger's value per work tick, a day of work a capability-day.</summary>
    private static readonly double CowDays = CowValue / SilverGapRules.ValuePerWorkTick / GameTime.TicksPerDay;

    private static PenRoamer Cow(string id, bool reachesEdge = true) => new(id, "Cow", MtbDays, CowValue, reachesEdge);

    private static ForecastCoupledContext Context(IReadOnlyList<PenRoamer>? roamers, IReadOnlyDictionary<int, string>? pens = null) =>
        PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Pen, [2] = PlannedRole.Workshop },
            PenHerds = pens ?? new Dictionary<int, string> { [1] = "Cow" },
            Roamers = roamers,
        };

    private static ForecastValueResult Ledger(ForecastCoupledContext context) =>
        ForecastValue.Of(PlannedRoomCreditTests.Outcome(context), context, GameTime.TicksPerDay);

    private static ForecastInvestmentContribution[] Credits(ForecastValueResult ledger) =>
        [.. ledger.InvestmentContributions.Where(r => r.Key.StartsWith(Wire.Name(PlannedRole.Pen) + "/", StringComparison.Ordinal))];

    [Fact]
    public void FourRoamingCowsAgainstAPlannedPenAreCreditedTheirValueOverTheRoamMtb()
    {
        var ledger = Ledger(Context([Cow("cow1"), Cow("cow2"), Cow("cow3"), Cow("cow4")]));
        var rows = Credits(ledger);
        Assert.Equal(["cow1", "cow2", "cow3", "cow4"], rows.Select(r => r.Key[(PlannedRoomCredits.Key(PlannedRole.Pen, 1, "").Length)..]));
        Assert.All(rows, r => Assert.Equal(("", Horizon - Served), (r.Pawn, r.Units)));
        Assert.Equal(-4 * CowDays / MtbDays * (Horizon - Served), rows.Sum(r => r.Value), 9);
        Assert.DoesNotContain(PlannedRoomCredits.Unassessed(PlannedRole.Pen, 1), ledger.Findings);
    }

    [Fact]
    public void NonRoamingAndEnclosedAnimalsEarnNothing()
    {
        // Chickens do not roam: native sends no Roam, so the census holds no roamer, and the pen earns nothing.
        Assert.Empty(Credits(Ledger(Context(AnimalUpkeepReads.ColonyRoamers(Census(Feed("chicken1"), Feed("chicken2")))))));
        // A cow already enclosed cannot reach the map edge, so it cannot roam and the pen stops nothing.
        Assert.Empty(Credits(Ledger(Context([Cow("cow1", reachesEdge: false)]))));
    }

    [Fact]
    public void APenTakesItsHerdsRoamersTheLowestIdFirstAndAMiscPenTheRest()
    {
        var alpaca = new PenRoamer("alpaca1", "Alpaca", 4, 200, true);
        var roamers = new[] { Cow("cow1"), alpaca };
        // The cows' pen takes the cow only.
        Assert.Equal(["cow1"], ForecastPens.PenCredit(new(1, "", 0), default!, Context(roamers), 0).Select(g => g.Subject));
        // A misc pen takes the roamers whose race has no pen of its own.
        var pens = new Dictionary<int, string> { [1] = "", [3] = "Cow" };
        Assert.Equal(["alpaca1"], ForecastPens.PenCredit(new(1, "", 0), default!, Context(roamers, pens), 0).Select(g => g.Subject));
        // With two pens of a herd the lowest id takes them.
        pens = new Dictionary<int, string> { [1] = "Cow", [3] = "Cow" };
        Assert.Single(ForecastPens.PenCredit(new(1, "", 0), default!, Context(roamers, pens), 0));
        Assert.Empty(ForecastPens.PenCredit(new(3, "", 0), default!, Context(roamers, pens), 0));
        Assert.Equal(200 / SilverGapRules.ValuePerWorkTick / GameTime.TicksPerDay / 4, ForecastPens.LossPerDay(alpaca), 12);
    }

    [Fact]
    public void AContextWithoutTheAnimalCensusIsARequiredFact()
    {
        var thrown = Assert.Throws<RequiredFactException>(() => ForecastPens.PenCredit(new(1, "", 0), default!, Context(null), 0));
        Assert.Equal("animal census", thrown.Fact);
    }

    [Fact]
    public void TheCoupledContextCarriesThePensAndRoamersTheRoundsRead()
    {
        // The production path (#3822): ColonyDecode fills AnimalUpkeep.Roamers from the census rows' Roam; Rounder fills
        // PlannedPens from the layout plan; GenerateCoupledActivities builds the context from the review's facts.
        var roamers = AnimalUpkeepReads.ColonyRoamers(Census(Feed("cow1", new() { Race = "Cow", MtbDays = MtbDays, MarketValue = CowValue, ReachesMapEdge = true }), Feed("chicken1")));
        var facts = ForecastCoupledTests.ConstructionReview() with
        {
            PlannedPens = new Dictionary<int, string> { [1] = "Cow" },
            AnimalUpkeep = new() { Roamers = roamers },
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        Assert.Same(facts.PlannedPens, context.PenHerds);
        Assert.Equal([Cow("cow1")], context.Roamers!);
        Assert.Single(ForecastPens.PenCredit(new(1, "", 0), default!, context, 0));
        // A fresh colony (an empty animal census) credits its planned pen nothing and throws nothing.
        var fresh = ForecastCoupledCandidates.GenerateCoupledActivities(facts with { AnimalUpkeep = new() { Roamers = AnimalUpkeepReads.ColonyRoamers(Census()) } })!.Context;
        Assert.Empty(ForecastPens.PenCredit(new(1, "", 0), default!, fresh, 0));
    }

    private static ColonyAnimal Feed(string id, AnimalRoam? roam = null) => new()
    {
        Pawn = new PawnId(id),
        Roping = roam is not null ? new AnimalRoping.Roped(roam, null) : new AnimalRoping.Unroped(),
    };

    private static ColonyFactsSnapshot Census(params ColonyAnimal[] animals) =>
        ColonyFactsCoreTests.Snapshot() with { Upkeep = UpkeepFactsTests.NoUpkeep with { Animals = [.. animals] } };
}
