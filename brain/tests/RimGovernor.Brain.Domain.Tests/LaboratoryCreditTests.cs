using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3800: a planned laboratory is credited with the research speed its room gives the bench: the bench's ResearchSpeedFactor
/// in the closed, clean room against its factor now (or the planned bench placed outdoors), times the research the schedule
/// does and tier 2's value of a research point.
/// </summary>
public sealed class LaboratoryCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Hour = GameTime.TicksPerHour;
    private const double Horizon = 10;
    private const double PointsPerTick = 0.01, PointValue = 0.002;
    private const string Bench = "SimpleResearchBench", Task = "research/pawn/bench1";
    private static readonly Cell InRoom = new(1, 1), Elsewhere = new(9, 9);

    /// <summary>The simple research bench's ResearchSpeedFactor as the 1.7 defs state it (Stats_Building_Special.xml, RoomStats.xml).</summary>
    internal static BenchSpeedModel SimpleBench() => new(0.75, 0.25,
    [
        new BenchOutdoorsPart(1, 0.75), new BenchRoomRolePart(RoomRole.Laboratory, 0.8), new BenchHeldPart(),
        new BenchCleanlinessPart([new(-5, 0.75), new(-2.5, 0.85), new(0, 1), new(1, 1.15)], 0.75), new BenchHeldPart(),
    ]);

    /// <summary>The bench in the open: no proper room (the room stat's roomless 0.75), outdoors (0.75).</summary>
    private static readonly double Unroofed = 0.75 * 0.75 * 0.75;

    /// <summary>The clean laboratory: cleanliness 0.5 is 1.075 on the curve, indoors, in its role.</summary>
    private static readonly double InTheLab = 0.75 * 1.075;

    private static PlannedRoomPrediction Prediction(string role = "Laboratory") => PlannedRoomPredictionTests.Closed("1", "WoodLog", role, 10d, 1, 9, 0, false) with { Cleanliness = 0.5 };

    /// <summary>Planned lab 1 (interior 0..2 x 0..2) served from day 3, one researcher at bench1 with the given factor now.</summary>
    private static ForecastCoupledContext Context(Cell bench, double factorNow, PlannedRoomPrediction? prediction = null) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            Activities = new()
            {
                Deadline = 0,
                Activities = [new(Task, "bench1", "bench1", []) { ProjectProgress = new("Project", PointsPerTick) }],
                Options = [new(Task, "pawn", Hour, 0d) { Equipment = ["bench1"] }],
            },
            Needs = ForecastValueTests.Context(ForecastValueTests.Pawn()).Needs with { ResearchBenches = [new("bench1", Bench, bench, factorNow)] },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Lab },
            Closures = [new(1, "WoodLog", [.. Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z)))], [], [],
                prediction ?? Prediction())],
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Lab } },
            ResearchBenchSpeeds = new Dictionary<string, BenchSpeedModel> { [Bench] = SimpleBench() },
            LaboratoryBench = Bench,
            Investment = new Dictionary<string, double> { ["Project"] = PointValue },
            InvestmentHorizonDays = Horizon,
        };

    private static ForecastCoupledOutcome Outcome(bool research = true) =>
        new(new(research ? [new(Task, "pawn", 0, 0, 0, Hour, 0, 0, Hour, 0, 0)] : [], Hour, 0), true, new Dictionary<string, ForecastNeedsResult>(), [])
        { ServedRooms = [new(1, "WoodLog", 3 * Day)] };

    private static ForecastValueResult Ledger(ForecastCoupledContext context, ForecastCoupledOutcome outcome) => ForecastValue.Of(outcome, context, Day);

    private static ForecastInvestmentContribution[] Credits(ForecastValueResult ledger) =>
        [.. ledger.InvestmentContributions.Where(r => r.Key.StartsWith("lab/", StringComparison.Ordinal))];

    [Fact]
    public void AResearcherAtAnUnroofedBenchGainsTheCleanLabsFactorTimesHoursTimesPointValue()
    {
        var row = Assert.Single(Credits(Ledger(Context(InRoom, Unroofed), Outcome())));
        // The factor gain, times the hour a day of research at a point a tick per unit of factor, times the point's value.
        var perDay = (InTheLab - Unroofed) * Hour * (PointsPerTick / Unroofed) * PointValue;
        Assert.Equal(("pawn", "lab/1/pawn", Horizon - 3), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-perDay * (Horizon - 3), row.Value, 12);
        Assert.Equal(InTheLab, SimpleBench().At(BenchRoom.Closed(RoomRole.Laboratory, 0.5)), 12);
        Assert.Equal(Unroofed, SimpleBench().At(BenchRoom.Outside), 12);
    }

    [Fact]
    public void WithNoBenchInTheRoomThePlannedBenchIsComparedWithItPlacedOutdoors()
    {
        // The researcher's bench stands elsewhere at 0.6: the room's own bench gains the clean lab over the outdoors.
        var row = Assert.Single(Credits(Ledger(Context(Elsewhere, 0.6), Outcome())));
        Assert.Equal(-(InTheLab - Unroofed) * Hour * (PointsPerTick / 0.6) * PointValue * (Horizon - 3), row.Value, 12);
        // A room the game would not call a laboratory loses the bench's room-role factor.
        var other = Assert.Single(Credits(Ledger(Context(Elsewhere, 0.6, Prediction("Workshop")), Outcome())));
        Assert.Equal(-(InTheLab * 0.8 - Unroofed) * Hour * (PointsPerTick / 0.6) * PointValue * (Horizon - 3), other.Value, 12);
    }

    [Fact]
    public void NoResearchProjectEarnsNothing()
    {
        var ledger = Ledger(Context(InRoom, Unroofed), Outcome(research: false));
        Assert.Empty(Credits(ledger));
        Assert.DoesNotContain(ledger.Findings, f => f.StartsWith("lab", StringComparison.Ordinal));
        // Nor does research tier 2 did not value.
        Assert.Empty(Credits(Ledger(Context(InRoom, Unroofed) with { Investment = new Dictionary<string, double>() }, Outcome())));
    }

    [Fact]
    public void AModdedRoomRoleIsAProperRoomOfNoLabRoleAndAnUnreadStatPartThrows()
    {
        // #3835: a predicted role RoomRole does not name (a modded one) is a proper room that is not a laboratory, credited as
        // any other role's room is.
        var modded = Assert.Single(Credits(Ledger(Context(Elsewhere, 0.6, Prediction("ModdedRoom")), Outcome())));
        Assert.Equal(Assert.Single(Credits(Ledger(Context(Elsewhere, 0.6, Prediction("Workshop")), Outcome()))).Value, modded.Value, 12);
        // A bench model with a stat part the catalog does not read throws a required fact.
        var unread = Context(InRoom, Unroofed) with
        {
            ResearchBenchSpeeds = new Dictionary<string, BenchSpeedModel> { [Bench] = SimpleBench() with { Parts = [new BenchUnreadPart("StatPart_Glow")] } },
        };
        Assert.Throws<RequiredFactException>(() => Ledger(unread, Outcome()));
    }

    [Fact]
    public void ALabIsPriced()
    {
        var context = Context(InRoom, Unroofed);
        Assert.True(context.Priced(1));
    }

    [Fact]
    public void TheCoupledContextCarriesTheLabFactsTheRoundsRead()
    {
        // The production path (#3822): GenerateCoupledActivities builds the context from the review's facts, which RoundsRead
        // fills from the catalog (ResearchBenchSpeeds, LaboratoryBench, Filth) and the layout plan (PlannedShelters).
        var facts = ForecastCoupledTests.ConstructionReview() with
        {
            PlannedShelters = [new() { Id = 1, Role = PlannedRole.Lab, Region = RegionRules.RectRegion(new(1, 1, 1, 1)) }],
            ResearchBenchSpeeds = new Dictionary<string, BenchSpeedModel> { [Bench] = SimpleBench() },
            LaboratoryBench = Bench,
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        Assert.Equal(PlannedRole.Lab, context.LayoutRooms[1].Role);
        Assert.Equal(Bench, context.LaboratoryBench);
        Assert.Same(facts.ResearchBenchSpeeds, context.ResearchBenchSpeeds);
        Assert.Same(facts.Filth, context.Filth);
        // A fresh colony's review (no research yet, no prediction of the planned lab) credits it nothing and throws nothing.
        Assert.Empty(ForecastLaboratory.LaboratoryCredit(new(1, "", Day), Outcome(research: false), [], context, Day));
        // With research but no prediction of the planned lab (a shape whose ring closes no room, #3834), it earns nothing.
        Assert.Empty(ForecastLaboratory.LaboratoryCredit(new(1, "", Day), Outcome(), [], context with
        {
            Activities = Context(InRoom, Unroofed).Activities, Investment = new Dictionary<string, double> { ["Project"] = PointValue },
        }, Day));
    }
}
