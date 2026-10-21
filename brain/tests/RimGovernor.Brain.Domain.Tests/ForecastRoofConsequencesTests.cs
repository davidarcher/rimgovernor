using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Roof and room consequences of a planned room (#3339): bed thoughts, the outdoors need, deterioration and bench speed.</summary>
public sealed class ForecastRoofConsequencesTests
{
    private const double Horizon = 30000;
    private static readonly Cell Inside = new(1, 1);
    private static readonly Cell WallCell = new(0, 1);
    private static readonly Cell DoorCell = new(2, 1);
    private const string Wall = "planned/room1/wall/WoodLog", Door = "planned/room1/door/WoodLog";

    /// <summary>An open bed cell now: no roof, no room (the outdoors).</summary>
    private static readonly NeedCell OpenCell = new(Known(false), Known(false), Known(false), Known(true), Known(true));

    private static NeedThoughtStage Stage(string thought, string role, int stage, double offset, double days = 1)
        => new(thought, role, stage, offset, Known(days * GameTime.TicksPerDay), false);

    private static readonly NeedThoughtStage[] BedStages =
    [
        Stage("SleptOutside", "slept_outside", 0, -4),
        Stage("SleptInBedroom", "slept_in_bedroom", 0, -4), Stage("SleptInBedroom", "slept_in_bedroom", 2, 2),
        Stage("SleptInBarracks", "slept_in_barracks", 0, -7), Stage("SleptInBarracks", "slept_in_barracks", 2, -4),
    ];

    private static PlannedBuildSite Ring(OpKind op, double finish)
    {
        var kind = op == OpKind.WallIn ? "wall" : "door";
        var id = op == OpKind.WallIn ? Wall : Door;
        var cell = op == OpKind.WallIn ? WallCell : DoorCell;
        var row = new ConstructionActivityOption(id, kind == "wall" ? "Wall" : "Door", cell, "pawn", "", "", cell, 0, 0, "Finish",
            Known(0.0), default, Known(finish)) { SiteApproachTicks = Known(1.0) };
        return new PlannedBuildSite(id, 1, op, kind == "wall" ? "Wall" : "Door", "WoodLog", Rotation.North, [cell])
            { Verdict = PlannedSiteVerdict.Feasible, Options = [row] };
    }

    /// <summary>One sleepy builder whose owned bed stands in the open where planned room 1 will be closed by a wall and a door.</summary>
    private static RoundsFacts Review(PlannedRoomPrediction prediction)
    {
        var facts = ForecastCoupledTests.ConstructionReview(stock: 100);
        var cells = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z))).ToArray();
        var open = new[] { Inside, WallCell, DoorCell };
        var thermal = new CoupledThermalRead
        {
            Outdoors = Known(20d), ThinRoofExchangePerTick = Known(.00005), WallExchangePerTick = Known(.000017),
            Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(20d), "none", !open.Contains(c), false, "", false, false, 0, false))),
            Workers = [new("pawn", Known(5d))], RoofRoutes = [new("pawn", Inside, Known(1d))],
        };
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Awake = Known(true), LocalHour = Known(6d), SleepThreshold = Known(.75), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(200d),
            ComfortableMin = Known(10d), ComfortableMax = Known(30d),
            MoodUnclampedTarget = Known(.5), MoodIntervalTicks = Known(150d), MoodRisePerTick = Known(.12 / GameTime.TicksPerHour),
            MoodFallPerTick = Known(.08 / GameTime.TicksPerHour), MoodFreezesWhileAsleep = Known(true), BreakThreshold = Known(.3),
            ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]), Memories = Known<EquatableArray<NeedMemory>>([]),
            Needs = [new("food", Known(1d), Known(0d), Known(.3)), new("rest", Known(.2), Known(0d), Known(.3))],
            // 0.8 rest at 0.00005 a tick: a 16000-tick sleep, long enough that ApplyBedThoughts always runs at the wake.
            Targets = [new("bed", "rest", "", 1, Known(0d), default, default, Known(.00005))
                { Location = Inside, Cell = OpenCell, Bed = new("None", Known(0), false) }],
            OwnedBed = "bed",
            ThoughtStages = EquatableArray.From(BedStages),
        };
        facts = facts with
        {
            PlannedShelters = [new() { Id = 1, Region = RegionRules.RectRegion(new(1, 1, 1, 1)), Door = DoorCell }],
            PlannedSites = Known(new PlannedSiteFacts { Sites = [Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30)], Routes = EquatableArray.From(cells
                .SelectMany(a => cells.Where(b => a != b).Select(b => new CoupledRoute("pawn", a, b, Known(1d))))
                .Concat(cells.Select(b => new CoupledRoute("pawn", b, b, Known(1d)) { FromStart = true }))) }),
            PlannedRoomPredictions = Known<EquatableArray<PlannedRoomPrediction>>([prediction]),
            ConstructionDeficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs with { Pawns = [pawn] },
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    Food = facts.ForecastObserved.Coupled.Food with { Options = [] }, FoodUses = [],
                    Thermal = thermal, Construction = facts.ForecastObserved.Coupled.Construction with { Options = [] },
                    Routes = EquatableArray.From(cells.SelectMany(a => cells.Where(b => a != b).Select(b => new CoupledRoute("pawn", a, b, Known(1d))))),
                },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon) };
    }

    private static PlannedRoomPrediction Closed(string role, int stage)
        => new("1", "WoodLog", role, Known(30d), Known(stage), 1, 1, false) { Beds = ["bed"] };

    /// <summary>The pawn's mood targets at the horizon over the feasible schedules of the full sleep and the given sites' roots.</summary>
    private static double[] MoodAtHorizon(RoundsFacts facts, params string[] sites)
    {
        facts = facts with { CoupledAlternatives = facts.CoupledAlternatives! with { Context = facts.CoupledAlternatives.Context with { MoodTrajectory = true } } };
        var candidates = facts.CoupledAlternatives!;
        var full = candidates.Context.NeedTasks.Where(n => n.Kind == "rest").OrderByDescending(n => n.Gain.Value).First();
        var roots = candidates.Alternatives.Where(c => c.ActivityCompletions.SequenceEqual([full.Task])
            || sites.Any(site => c.Id.StartsWith(site + "/composition/", StringComparison.Ordinal) && !c.Id.Contains("/after/", StringComparison.Ordinal))).ToArray();
        Assert.Equal(sites.Length + 1, roots.Length);
        var forecast = ColonyForecastRules.ForecastColony(ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay, facts, 
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new() { Add = EquatableArray.From(roots) }));
        var moods = forecast.Proposed.Coupled!.Alternatives.Where(a => a.Feasible == Known(true))
            .Select(a => a.Needs["pawn"].Mood).Where(m => m is { Trajectory.Count: > 0 }).Select(m => m!.Trajectory[^1].Target.Value).ToArray();
        Assert.NotEmpty(moods);
        return moods;
    }

    [Fact]
    public void AClosedPlannedBedroomBeatsItsWallsAloneOnMood()
    {
        // The wall alone leaves the bed outdoors (SleptOutside, -4); wall and door close the predicted bedroom before the
        // wake (SleptInBedroom at its stage, +2). The mood itself differs, whatever development the extra site credits.
        var facts = Review(Closed("Bedroom", 2));
        var wallsOnly = MoodAtHorizon(facts, Wall).Max();
        var closed = MoodAtHorizon(facts, Wall, Door).Max();
        Assert.Equal(.06, closed - wallsOnly, 3);
    }

    [Fact]
    public void AnAwfulPlannedBarracksHonestlyLosesToSleepingOutside()
    {
        // Closing a barracks predicted at stage 0 (awful, -7) before the wake is worse for the owner than sleeping outside (-4);
        // only a schedule that closes it after the wake keeps the outside thought.
        var facts = Review(Closed("Barracks", 0));
        var wallsOnly = MoodAtHorizon(facts, Wall);
        var closed = MoodAtHorizon(facts, Wall, Door);
        Assert.Equal(wallsOnly.Min(), wallsOnly.Max(), 6);
        Assert.Equal(-.03, closed.Min() - wallsOnly.Max(), 3);
        Assert.True(closed.Max() <= wallsOnly.Max() + 1e-9);
    }

    [Fact]
    public void AnUnknownPredictedStageLeavesTheOwnersMoodUnassessed()
    {
        var facts = Review(Closed("Bedroom", 2) with { Stage = default });
        var candidates = facts.CoupledAlternatives!;
        var full = candidates.Context.NeedTasks.Where(n => n.Kind == "rest").OrderByDescending(n => n.Gain.Value).First();
        var roots = candidates.Alternatives.Where(c => c.ActivityCompletions.SequenceEqual([full.Task])
            || (c.Id.StartsWith(Wall + "/composition/", StringComparison.Ordinal) || c.Id.StartsWith(Door + "/composition/", StringComparison.Ordinal))
            && !c.Id.Contains("/after/", StringComparison.Ordinal)).ToArray();
        var forecast = ColonyForecastRules.ForecastColony(ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay, facts, 
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new() { Add = EquatableArray.From(roots) }));
        var closing = forecast.Proposed.Coupled!.Alternatives.Where(a => a.Feasible == Known(true) && a.PlannedRooms.Any(r => r.CompletedSites.Count == 2)
            && a.Plan.Allocations.Where(x => x.Activity == full.Task).All(x => a.Plan.Allocations.Where(s => candidates.Context.PlannedSites.Any(p => p.Finishes.Contains(s.Activity))).All(s => s.End <= x.End)));
        Assert.All(closing, a => Assert.Contains(a.Needs["pawn"].Findings, f => f.StartsWith("sleep_room_stage_unknown:", StringComparison.Ordinal)));
        Assert.All(closing, a => Assert.Null(a.Needs["pawn"].Mood));
    }

    [Fact]
    public void ARenewalEndsTheObservedSleepMemoryAtTheWake()
    {
        // An observed SleptOutside with half a day left is ended at the wake and replaced by the bedroom thought.
        var observed = new NeedMemory("SleptOutside", -4, Known(30000d), false);
        var renewal = new ForecastMemoryRenewal(10000, ["SleptOutside", "SleptInBedroom"], [new("SleptInBedroom", 2, Known(60000d), false)]);
        var (ends, gains) = ForecastMood.RenewalSchedule([observed], [renewal], [])!.Value;
        Assert.Equal(10000, ends["SleptOutside"]);
        var gain = Assert.Single(gains);
        Assert.Equal((10000d, double.PositiveInfinity), (gain.Start, gain.End));
        // A meal memory of a renewed def is not additive evidence.
        Assert.Null(ForecastMood.RenewalSchedule([observed], [renewal], ["SleptInBedroom"]));
        // A later renewal of the same def ends the earlier gain.
        var (_, twice) = ForecastMood.RenewalSchedule([], [renewal, renewal with { At = 20000 }], [])!.Value;
        Assert.Equal([20000d, double.PositiveInfinity], twice.Select(g => g.End));
    }

    private static ForecastClosedRoom Room(double closedAt, double roofedAt, int open = 4)
        => new(1, "WoodLog", [Inside], new("1", "WoodLog", "Bedroom", Known(30d), Known(2), 4, open, false), closedAt, roofedAt);

    [Fact]
    public void ForAPawnWhoNeedsTheOutdoorsAPlannedRoofIsACost()
    {
        // Joy at a cell of the planned room for 12000 ticks, the outdoors need at 0.85 (Free above 0.8). Under the planned
        // roof the room is indoors under a thin roof (-0.32 per interval): the level falls into NeedFreshAir, a mood cost.
        // Closed but unroofed it is indoors with no roof (+5): the need stays free and costs nothing.
        var pawn = new NeedsForecastPawn
        {
            Pawn = "pawn", OutdoorsLevel = Known(.85),
            Targets = [new("pin", "joy", "", 1, Known(0d), default, default, Known(.001)) { Location = Inside, Cell = OpenCell }],
            ThoughtStages = [Stage("NeedOutdoors", "need_outdoors", 4, -3), Stage("NeedOutdoors", "need_outdoors", 3, -5)],
        };
        ForecastNeedChoice[] joy = [new("joy", "pawn", "joy", "pin", "", 0, 0, 0, 12000, 1, [])];
        var observed = Known<EquatableArray<MoodThought>>([]);
        var roofed = ForecastRoofConsequences.RoofMoodOf(pawn, new(), joy, [Room(0, 0)], observed, Horizon);
        Assert.Equal("", roofed.Unassessed);
        var (thought, spans) = Assert.Single(roofed.Banded);
        Assert.Equal("NeedOutdoors", thought);
        var span = Assert.Single(spans);
        // 0.05 of level at 0.32 * 0.0025 per 150 ticks: 9375 ticks to leave Free, then NeedFreshAir to the horizon.
        Assert.Equal(9375, span.Start, 6);
        Assert.Equal((Horizon, -3d), (span.End, span.OffsetDelta));

        var unroofed = ForecastRoofConsequences.RoofMoodOf(pawn, new(), joy, [Room(0, double.PositiveInfinity)], observed, Horizon);
        Assert.All(unroofed.Banded, b => Assert.Empty(b.Spans));
    }

    [Fact]
    public void ADayOfRainDestroysAnOpenFoodStackThatAPlannedRoofSaves()
    {
        // Observed in the open in full rain: (0.5 unroofed + 0.5 outdoors) * Lerp(1, 5, 1) = 5 times a base rate of 2,
        // 10 hit points a day. 8 hit points last 0.8 of a day on a 250x250 map (38 cells a tick).
        var read = new NeedsForecastRead { RainRate = Known(1d), MapArea = Known(62500), Deterioration = new(.5, .5, 0) };
        var stack = new NeedTarget("meal", "food", "MealSimple", 5, Known(0d), default, default, default)
            { Location = Inside, Cell = OpenCell, Deterioration = new(8, Known(10d), true, 0, false) };
        var perTick = 10 / 36d * 38 / 62500;
        Assert.Equal(8 / perTick, ForecastRoofConsequences.DeterioratedAt(stack, read, [], GameTime.TicksPerDay)!.Value, 6);
        Assert.True(8 / perTick < GameTime.TicksPerDay);
        // A planned room closed and roofed over the stack at 6000 ticks: roofed and indoors, it stops deteriorating.
        Assert.Equal(double.PositiveInfinity, ForecastRoofConsequences.DeterioratedAt(stack, read, [Room(6000, 6000)], GameTime.TicksPerDay));
        // Closed but open to the sky: the outdoors offset is gone (one open cell of four is under a quarter), the rain stays.
        var closedOpen = ForecastRoofConsequences.DeterioratedAt(stack, read, [Room(6000, double.PositiveInfinity, open: 0)], 2 * GameTime.TicksPerDay)!.Value;
        Assert.Equal(6000 + (8 - 6000 * perTick) / (perTick / 2), closedOpen, 6);
        // An unread rate stays unknown.
        Assert.Null(ForecastRoofConsequences.DeterioratedAt(stack with { Deterioration = stack.Deterioration! with { RatePerDay = default } }, read, [], GameTime.TicksPerDay));
    }

    [Fact]
    public void ABenchInAnUnclosedPlannedRoomGetsAFasterClosureConditionalVariant()
    {
        var closure = new ForecastPlannedClosure(1, "WoodLog", [Inside], [Wall, Door], [],
            new("1", "WoodLog", "Workshop", Known(10d), Known(1), 4, 4, false)
            { Benches = [new("bench", true), new("indoor-bench", false)] });
        var realized = new[]
        {
            new ForecastPlannedSite(Wall, 1, "WoodLog", ["wall/deliver", "wall/finish"], ["wall/finish"]),
            new ForecastPlannedSite(Door, 1, "WoodLog", ["door/finish"], ["door/finish"]),
        };
        var tasks = new List<ForecastActivity> { new("cook", "cell/1/1", "cell/1/1", ["fetch"]), new("craft", "cell/1/1", "cell/1/1", []) };
        var options = new List<ForecastActivityOption>
        {
            new("cook", "pawn", Known(1000d), Known(0d)) { Equipment = ["bench"] },
            new("craft", "pawn", Known(1000d), Known(0d)) { Equipment = ["indoor-bench"] },
        };
        var roots = new[] { new ForecastCommitment("cook-root", []) { ActivityCompletions = ["cook"] }, new ForecastCommitment("craft-root", []) { ActivityCompletions = ["craft"] } };
        var alternatives = roots.ToList();
        ForecastRoofConsequences.AddClosedBenchVariants([closure], roots, realized, tasks, options, new(), alternatives, new Dictionary<string, (int, string)>());

        var variant = Assert.Single(tasks, t => t.Id == "cook/closed/1/WoodLog");
        Assert.Equal(["fetch", "wall/finish", "door/finish"], variant.Prerequisites);
        Assert.Equal(800, Assert.Single(options, o => o.Activity == variant.Id).WorkTicks.Value, 6);
        Assert.Equal(["cook"], Assert.Single(alternatives, a => a.Id == "cook-root/closed/1/WoodLog").ActivityCompletions.Select(_ => "cook"));
        // A bench already indoors, or a room still outdoors once closed (300 open roof cells), gets no variant.
        Assert.DoesNotContain(tasks, t => t.Id.StartsWith("craft/closed", StringComparison.Ordinal));
        var huge = closure with { Prediction = closure.Prediction with { Cells = 400, OpenRoofCells = 300 } };
        var before = tasks.Count;
        ForecastRoofConsequences.AddClosedBenchVariants([huge], roots, realized, tasks, options, new(), alternatives, new Dictionary<string, (int, string)>());
        Assert.Equal(before, tasks.Count);
    }
}
