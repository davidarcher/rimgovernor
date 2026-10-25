using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Roof and room consequences of a planned room (#3339): bed thoughts, the outdoors need, deterioration and bench speed.</summary>
public sealed class ForecastRoofConsequencesTests
{
    [Theory]
    [InlineData(false, 6000, false, 3)]
    [InlineData(true, 0, false, 3)]
    [InlineData(false, 6000, true, 2)]
    [InlineData(true, 0, true, 3)]
    public void ADeathrestChamberThoughtStartsAtItsActualWakeIncludingInterruption(bool interrupted, double wake, bool closes, double offset)
    {
        var pawn = NeedsFixtures.Pawn() with
        {
            Awake = false, Mood = NeedsFixtures.Mood, Needs = [NeedsFixtures.Rate("rest", 1, 0, .3)],
            LyingIn = new(new("bed", false, false, .00002, 0, 20), false, new LyingInDeathrest.Wakes(6000, new DeathrestSunlight.Shaded())) { DeathrestWakeStage = 5 },
            ThoughtStages = [new("DeathrestChamber", ForecastDeathrest.ChamberThoughtRole, 4, 2, NeedsFixtures.Situational),
                new("DeathrestChamber", ForecastDeathrest.ChamberThoughtRole, 5, 3, NeedsFixtures.Situational)],
        };
        ForecastNeedChoice[] choices = interrupted ? [] : [new("need/pawn/observed_sleep", "pawn", "rest", "bed", "", 0, 0, 0, 6000, .12, [])];
        if (!interrupted)
            Assert.Equal(wake, ForecastNeeds.SleepServiceEnds(pawn, choices, null, new SortedSet<string>(StringComparer.Ordinal))![choices[0].Id]);
        ForecastClosedRoom[] rooms = closes ? [new([pawn.Location], Closed("Bedroom", 3), 3000, 3000)] : [];
        var mood = ForecastRoofConsequences.RoofMoodOf(pawn, NeedsFixtures.Empty, choices, rooms,
            new Dictionary<string, EquatableArray<MoodThought>> { ["pawn"] = [] }, 10000);
        var thought = Assert.Single(mood.Banded);
        Assert.Equal("DeathrestChamber", thought.Thought);
        Assert.Equal((wake, 10000d, offset), Assert.Single(thought.Spans));
    }

    internal const double Horizon = 30000;
    internal static readonly Cell Inside = new(1, 1);
    private static readonly Cell WallCell = new(0, 1);
    private static readonly Cell DoorCell = new(2, 1);
    internal const string Wall = "planned/room1/wall/WoodLog", Door = "planned/room1/door/WoodLog";

    /// <summary>An open bed cell now: no roof, no room (the outdoors).</summary>
    private static readonly NeedCell OpenCell = new(false, false, false, true, true);

    private static NeedThoughtStage Stage(string thought, string role, int stage, double offset, double days = 1)
        => new(thought, role, stage, offset, NeedsFixtures.Lasting(days * GameTime.TicksPerDay, false));

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
        var row = new Row(id, kind == "wall" ? "Wall" : "Door", cell, "pawn", "", "", cell, 0, 0, "Finish",
            0.0, default, finish) { SiteApproachTicks = 1.0 };
        return new PlannedBuildSite(id, 1, op, kind == "wall" ? "Wall" : "Door", "WoodLog", Rotation.North, [cell])
            { Answer = ConstructionFixtures.Answer(id) with { Options = [row] } };
    }

    /// <summary>One sleepy builder whose owned bed stands in the open where planned room 1 will be closed by a wall and a door;
    /// <paramref name="sleeper"/> changes the pawn and <paramref name="plan"/> the facts before the candidates are generated.</summary>
    internal static ReviewFacts Review(PlannedRoomPrediction prediction, Func<NeedsForecastPawn, NeedsForecastPawn>? sleeper = null,
        Func<ReviewFacts, ReviewFacts>? plan = null)
    {
        var facts = ForecastCoupledTests.ConstructionReview(stock: 100);
        var cells = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z))).ToArray();
        var open = new[] { Inside, WallCell, DoorCell };
        var thermal = CoupledThermalTests.NoThermal with
        {
            Outdoors = 20d, ThinRoofExchangePerTick = .00005, WallExchangePerTick = .000017,
            Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, 20d, "none", !open.Contains(c), false, "", false, false, 0, false))),
            Workers = [new("pawn", 5d)], RoofRoutes = [new("pawn", Inside, new RoofApproach.Path(1d))],
        };
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Awake = true, LocalHour = 6d, SleepThreshold = .75, WakeThreshold = 1d, SleepBlockedUntilTicks = 200d,
            ComfortableMin = 10d, ComfortableMax = 30d,
            Mood = (facts.ForecastObserved.Needs!.Pawns[0].Mood ?? NeedsFixtures.Mood) with
            {
                UnclampedTarget = .5, RisePerTick = .12 / GameTime.TicksPerHour, FallPerTick = .08 / GameTime.TicksPerHour,
                FreezesWhileAsleep = true, BreakThreshold = .3, Conditional = new ConditionalMood.Modeled([]), Memories = [],
            },
            Needs = [NeedsFixtures.Rate("food", 1d, 0d, .3), NeedsFixtures.Rate("rest", .2, 0d, .3)],
            // 0.8 rest at 0.00005 a tick: a 16000-tick sleep, long enough that ApplyBedThoughts always runs at the wake.
            Targets = [NeedsFixtures.Service("bed", "rest", 0d, .00005) with
                { Location = Inside, Cell = OpenCell, Bed = NeedsFixtures.BedRoom("None", 0, false) }],
            OwnedBed = "bed", OwnedBedRoom = NeedsFixtures.BedRoom("None", 0, false),
            ThoughtStages = EquatableArray.From(BedStages),
        };
        facts = facts with
        {
            PlannedShelters = [new() { Id = 1, Region = RegionRules.RectRegion(new(1, 1, 1, 1)), Door = DoorCell }],
            PlannedSites = new PlannedSiteFacts { Sites = [Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30)], Routes = EquatableArray.From(cells
                .SelectMany(a => cells.Where(b => a != b).Select(b => new CoupledRoute("pawn", a, b, new RouteTicks.Reachable(1d)) { FromStart = false }))
                .Concat(cells.Select(b => new CoupledRoute("pawn", b, b, new RouteTicks.Reachable(1d)) { FromStart = true }))) },
            PlannedRoomPredictions = [prediction],
            CurrentConstruction = CurrentConstruction.Empty with { Deficit = new Dictionary<Resource, long>() },
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs with { Pawns = [sleeper is null ? pawn : sleeper(pawn)] },
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    Food = facts.ForecastObserved.Coupled.Food with { Options = [] }, FoodUses = [],
                    Thermal = thermal, Construction = facts.ForecastObserved.Coupled.Construction with { Options = [] },
                    Routes = EquatableArray.From(cells.SelectMany(a => cells.Where(b => a != b).Select(b => new CoupledRoute("pawn", a, b, new RouteTicks.Reachable(1d)) { FromStart = false }))),
                },
            },
        };
        if (plan is not null) facts = plan(facts);
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon) };
    }

    internal static PlannedRoomPrediction Closed(string role, int stage)
        => PlannedRoomPredictionTests.Closed("1", "WoodLog", role, 30d, stage, 1, 1, false) with { Beds = ["bed"] };

    /// <summary>The pawn's mood targets at the horizon over the feasible schedules of the full sleep and the given sites' roots.</summary>
    private static double[] MoodAtHorizon(ReviewFacts facts, params string[] sites)
    {
        facts = facts with { CoupledAlternatives = facts.CoupledAlternatives! with { Context = facts.CoupledAlternatives.Context with { MoodTrajectory = true } } };
        var candidates = facts.CoupledAlternatives!;
        var full = candidates.Context.NeedTasks.Where(n => n.Kind == "rest").OrderByDescending(n => n.Gain).First();
        var roots = candidates.Alternatives.Where(c => c.ActivityCompletions.SequenceEqual([full.Task])
            || sites.Any(site => c.Id.StartsWith(site + "/composition/", StringComparison.Ordinal) && !c.Id.Contains("/after/", StringComparison.Ordinal))).ToArray();
        Assert.Equal(sites.Length + 1, roots.Length);
        var forecast = CoupledForecastKit.Forecast(ForecastCoupled.CoupledForecastRequestOf(new(100), Horizon / GameTime.TicksPerDay, facts, 
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), roots);
        var moods = forecast.Proposed.Coupled!.Alternatives.Where(a => a.Feasible)
            .Select(a => a.Needs["pawn"].Mood).OfType<ForecastMoodResult.Projected>().Where(m => m.Trajectory.Count > 0).Select(m => m.Trajectory[^1].Target).ToArray();
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
    public void ARenewalEndsTheObservedSleepMemoryAtTheWake()
    {
        // An observed SleptOutside with half a day left is ended at the wake and replaced by the bedroom thought.
        var observed = NeedsFixtures.Memory("SleptOutside", -4, 30000d, false);
        var renewal = new ForecastMemoryRenewal(10000, ["SleptOutside", "SleptInBedroom"], [NeedsFixtures.Memory("SleptInBedroom", 2, 60000d, false)]);
        var (ends, gains) = ForecastMood.RenewalSchedule([observed], [renewal], [])!.Value;
        Assert.Equal((1d, 0d), (ends["SleptOutside"].At(9999), ends["SleptOutside"].At(10000)));
        var gain = Assert.Single(gains);
        Assert.Equal((10000d, 1d), (gain.Start, gain.Share.At(double.PositiveInfinity)));
        // A meal memory of a renewed def is not additive evidence.
        Assert.Null(ForecastMood.RenewalSchedule([observed], [renewal], ["SleptInBedroom"]));
        // A later renewal of the same def ends the earlier gain.
        var (_, twice) = ForecastMood.RenewalSchedule([], [renewal, renewal with { At = 20000 }], [])!.Value;
        Assert.Equal([0d, 1d], twice.Select(g => g.Share.At(20000)));
    }

    [Fact]
    public void AChanceRenewalKeepsTheRestOfTheMemoryItMayReplace()
    {
        // A renewal at chance 0.4 (a sleep inside the bed-thought onset window) keeps 0.6 of the observed memory and gives 0.4
        // of its gain; a later certain renewal of the def ends both.
        var observed = NeedsFixtures.Memory("SleptOutside", -4, 60000d, false);
        var renewal = new ForecastMemoryRenewal(10000, ["SleptOutside"], [NeedsFixtures.Memory("SleptOutside", -4, 60000d, false)]) { Chance = .4 };
        var (ends, gains) = ForecastMood.RenewalSchedule([observed], [renewal, renewal with { At = 20000, Chance = 1 }], [])!.Value;
        var share = ends["SleptOutside"];
        Assert.Equal(1, share.At(0), 9);
        Assert.Equal(.6, share.At(10000), 9);
        Assert.Equal(0, share.At(20000), 9);
        Assert.Equal(2, gains.Count);
        Assert.Equal(.4, gains[0].Share.At(10000), 9);
        Assert.Equal(0, gains[0].Share.At(20000), 9);
        Assert.Equal(1, gains[1].Share.At(20000), 9);
        // A chance outside (0, 1] is no renewal the game makes.
        Assert.Null(ForecastMood.RenewalSchedule([observed], [renewal with { Chance = 0 }], []));
    }

    [Fact]
    public void BedThoughtsComeWithTheOnsetTheGameDraws()
    {
        // A fresh sleep draws its onset from Rand.Range(2500, 10000): none under 2500 ticks, (floor(s) - 2499) / 7500 inside,
        // always from 10000. A sleep observed now gives them as the native read says.
        ForecastNeedChoice Rest(double start) => new("rest", "pawn", "rest", "bed", "", 0, 0, start, start + 1, 1, []);
        var awake = NeedsFixtures.Pawn();
        Assert.Equal(0, ForecastRoofConsequences.BedThoughtsChance(awake, Rest(100), 2599));
        Assert.Equal(1d / 7500, ForecastRoofConsequences.BedThoughtsChance(awake, Rest(100), 2600), 12);
        Assert.Equal(.5, ForecastRoofConsequences.BedThoughtsChance(awake, Rest(0), 6249), 12);
        Assert.Equal(1, ForecastRoofConsequences.BedThoughtsChance(awake, Rest(0), 12000));
        var asleep = awake with { Awake = false };
        Assert.Equal(1, ForecastRoofConsequences.BedThoughtsChance(asleep with { BedThoughts = new BedThoughtOnset.Given() }, Rest(0), 100));
        Assert.Equal(1, ForecastRoofConsequences.BedThoughtsChance(asleep with { BedThoughts = new BedThoughtOnset.Due(50) }, Rest(0), 100));
        Assert.Equal(0, ForecastRoofConsequences.BedThoughtsChance(asleep with { BedThoughts = new BedThoughtOnset.Due(500) }, Rest(0), 100));
        Assert.Equal(0, ForecastRoofConsequences.BedThoughtsChance(asleep, Rest(0), 20000));
    }

    [Fact]
    public void APawnWokenAtOnceGetsItsGivenBedThoughtsWhereItLies()
    {
        // Asleep now with no rest from tick 0, the outcome wakes the pawn there (#3882): FinalizeLayingJob gives the bed
        // thoughts again once they were given, from the lie-down's room and the owned bed's; a due onset gives none.
        var asleep = NeedsFixtures.Pawn() with
        {
            Awake = false, BedThoughts = new BedThoughtOnset.Given(), OwnedBed = "bed", OwnedBedRoom = NeedsFixtures.BedRoom("Bedroom", 2, false),
            LyingIn = new(new("bed", true, false, .00005, 0, 20), false, new LyingInDeathrest.None()) { DeathrestWakeStage = -1 }, ThoughtStages = EquatableArray.From(BedStages),
        };
        var renewal = Assert.Single(ForecastRoofConsequences.RoofMoodOf(asleep, NeedsFixtures.Empty, [], [], NoThoughts(asleep), Horizon).Renewals);
        Assert.Equal((0d, 1d), (renewal.At, renewal.Chance));
        Assert.Equal(["SleptOutside", "SleptInBedroom"], renewal.Gains.Select(g => g.Id));
        Assert.Equal([2d], renewal.Gains.Where(g => g.Id == "SleptInBedroom").Select(g => g.Offset));
        // On the ground: SleptOutside alone. A due onset or an awake pawn: no renewal.
        var ground = ForecastRoofConsequences.RoofMoodOf(asleep with { LyingIn = asleep.LyingIn! with { Place = asleep.LyingIn.Place with { Bed = "" } } },
            NeedsFixtures.Empty, [], [], NoThoughts(asleep), Horizon);
        Assert.Equal(["SleptOutside"], Assert.Single(ground.Renewals).Gains.Select(g => g.Id));
        Assert.Empty(ForecastRoofConsequences.RoofMoodOf(asleep with { BedThoughts = new BedThoughtOnset.Due(10) }, NeedsFixtures.Empty, [], [],
            NoThoughts(asleep), Horizon).Renewals);
        // Without the lie-down read, the woken pawn's bed thoughts are a required fact.
        Assert.Throws<RequiredFactException>(() => ForecastRoofConsequences.RoofMoodOf(asleep with { LyingIn = null }, NeedsFixtures.Empty, [], [],
            NoThoughts(asleep), Horizon));
    }

    /// <summary>
    /// #3883: where an observed deathrest ends (Gene_Deathrest.TickDeathresting). An auto-wake ends it at its remaining ticks, a
    /// held or paused one holds to the horizon; an open bed's first sunlit check wakes it first unless the outcome's planned roof
    /// covers the bed by then, and a bed unlit through native's scan short of the horizon is an unread fact.
    /// </summary>
    [Fact]
    public void ADeathrestEndsAtItsWakeOrItsBedsFirstSunlight()
    {
        var findings = new List<string>();
        double? End(LyingInDeathrest d, params ForecastClosedRoom[] rooms) => ForecastCoupled.CoupledSearchCache.DeathrestEnd(d, rooms, Horizon, findings.Add);
        DeathrestSunlight sunlit = new DeathrestSunlight.Sunlit([Inside], 3000) { NotModelled = ["deathrest_sunlight_lightning_not_modelled"] };
        Assert.Equal(6000, End(new LyingInDeathrest.Wakes(6000, new DeathrestSunlight.Shaded())));
        Assert.Equal(Horizon, End(new LyingInDeathrest.Wakes(2 * Horizon, new DeathrestSunlight.Shaded())));
        Assert.Equal(Horizon, End(new LyingInDeathrest.Held(new DeathrestSunlight.Shaded())));
        Assert.Equal(Horizon, End(new LyingInDeathrest.Paused()));
        Assert.Empty(findings);
        Assert.Equal(3000, End(new LyingInDeathrest.Wakes(6000, sunlit)));
        Assert.Equal(3000, End(new LyingInDeathrest.Held(sunlit)));
        // Roofed at 2000, before the sunlit check: the roof stays, so only the auto-wake ends it. Roofed after it: the sun wakes it.
        Assert.Equal(6000, End(new LyingInDeathrest.Wakes(6000, sunlit), Room(1000, 2000)));
        Assert.Equal(3000, End(new LyingInDeathrest.Wakes(6000, sunlit), Room(1000, 4000)));
        // Sunlit only after the wake: the wake ends it.
        Assert.Equal(6000, End(new LyingInDeathrest.Wakes(6000, new DeathrestSunlight.Sunlit([Inside], 8000))));
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("deathrest_sunlight_lightning_not_modelled", f));
        // Unlit through a scan that outlasts the wake ends at the wake; one the held deathrest outlasts is unread.
        Assert.Equal(6000, End(new LyingInDeathrest.Wakes(6000, new DeathrestSunlight.Unlit(10000))));
        Assert.Null(End(new LyingInDeathrest.Held(new DeathrestSunlight.Unlit(10000))));
    }

    /// <summary>The pawn's observed thoughts read, none held.</summary>
    private static Dictionary<string, EquatableArray<MoodThought>> NoThoughts(NeedsForecastPawn pawn) => new() { [pawn.Pawn] = [] };

    private static ForecastClosedRoom Room(double closedAt, double roofedAt, int open = 4)
        => new([Inside],PlannedRoomPredictionTests.Closed("1", "WoodLog", "Bedroom", 30d, 2, 4, open, false), closedAt, roofedAt);

    [Fact]
    public void ForAPawnWhoNeedsTheOutdoorsAPlannedRoofIsACost()
    {
        // Joy at a cell of the planned room for 12000 ticks, the outdoors need at 0.85 (Free above 0.8). Under the planned
        // roof the room is indoors under a thin roof (-0.32 per interval): the level falls into NeedFreshAir, a mood cost.
        // Closed but unroofed it is indoors with no roof (+5): the need stays free and costs nothing.
        var pawn = NeedsFixtures.Pawn() with {
            Pawn = "pawn", Outdoors = new NeedLevel.Has(.85),
            Targets = [NeedsFixtures.Service("pin", "joy", 0d, .001) with { Location = Inside, Cell = OpenCell }],
            ThoughtStages = [Stage("NeedOutdoors", "need_outdoors", 4, -3), Stage("NeedOutdoors", "need_outdoors", 3, -5)],
        };
        ForecastNeedChoice[] joy = [new("joy", "pawn", "joy", "pin", "", 0, 0, 0, 12000, 1, [])];
        var observed = NoThoughts(pawn);
        var roofed = ForecastRoofConsequences.RoofMoodOf(pawn, NeedsFixtures.Empty, joy, [Room(0, 0)], observed, Horizon);
        var (thought, spans) = Assert.Single(roofed.Banded);
        Assert.Equal("NeedOutdoors", thought);
        var span = Assert.Single(spans);
        // 0.05 of level at 0.32 * 0.0025 per 150 ticks: 9375 ticks to leave Free, then NeedFreshAir to the horizon.
        Assert.Equal(9375, span.Start, 6);
        Assert.Equal((Horizon, -3d), (span.End, span.OffsetDelta));

        var unroofed = ForecastRoofConsequences.RoofMoodOf(pawn, NeedsFixtures.Empty, joy, [Room(0, double.PositiveInfinity)], observed, Horizon);
        Assert.All(unroofed.Banded, b => Assert.Empty(b.Spans));
    }

    [Fact]
    public void ADayOfRainDestroysAnOpenFoodStackThatAPlannedRoofSaves()
    {
        // Observed in the open in full rain: (0.5 unroofed + 0.5 outdoors) * Lerp(1, 5, 1) = 5 times a base rate of 2,
        // 10 hit points a day. 8 hit points last 0.8 of a day on a 250x250 map (38 cells a tick).
        var read = NeedsFixtures.Empty with { RainRate = 1d, MapArea = 62500, Deterioration = new(.5, .5, 0) };
        var stack = NeedsFixtures.Target("meal", "food", "MealSimple", 5, 0d, new NeedRecovery.Rate(0)) with
            { Location = Inside, Cell = OpenCell, Deterioration = new(8, 10d, true, 0, false) };
        var perTick = 10 / 36d * 38 / 62500;
        Assert.Equal(8 / perTick, ForecastRoofConsequences.DeterioratedAt(stack, read, [], GameTime.TicksPerDay)!.Value, 6);
        Assert.True(8 / perTick < GameTime.TicksPerDay);
        // A planned room closed and roofed over the stack at 6000 ticks: roofed and indoors, it stops deteriorating.
        Assert.Equal(double.PositiveInfinity, ForecastRoofConsequences.DeterioratedAt(stack, read, [Room(6000, 6000)], GameTime.TicksPerDay));
        // Closed but open to the sky: the outdoors offset is gone (one open cell of four is under a quarter), the rain stays.
        var closedOpen = ForecastRoofConsequences.DeterioratedAt(stack, read, [Room(6000, double.PositiveInfinity, open: 0)], 2 * GameTime.TicksPerDay)!.Value;
        Assert.Equal(6000 + (8 - 6000 * perTick) / (perTick / 2), closedOpen, 6);
    }

    [Fact]
    public void APlacedShellIsPredictedFromItsPlacedRingSites()
    {
        // The wall's blueprint stands on the ring and the bed's inside it (#3537): the room is asked with the wall in its own
        // stuff although it owes no planned site, and only the ring site is the room's.
        PlannedRoom room = new() { Id = 1, Region = RegionRules.RectRegion(new(1, 1, 1, 1)), Door = DoorCell };
        ConstructionSite[] placed =
        [
            ConstructionObservationFixtures.Site(new("Wall", WallCell, Rotation.North, "WoodLog"), id: "Thing_Blueprint_Wall1"),
            ConstructionObservationFixtures.Site(new("Bed", Inside, Rotation.North, "WoodLog"), id: "Thing_Blueprint_Bed2"),
        ];
        Assert.Equal(["Thing_Blueprint_Wall1"], PlannedRules.PlacedRing(room, placed).Select(s => s.Id));
        var shape = Assert.Single(PlannedRules.PlannedRoomPredictionRequestOf([room], [], placed));
        Assert.Equal([new PlannedRoomPiece(WallCell, "Wall", "WoodLog")], shape.Pieces);
        Assert.Empty(PlannedRules.PlannedRoomPredictionRequestOf([room], [], placed.Skip(1)));
    }

    [Fact]
    public void APlacedShellClosesOnceEachPlacedRingSiteIsFinished()
    {
        // Placed ring sites close the room as planned ones do (#3537): at the latest of their earliest finishes, any finish
        // variant counting; one unfinished keeps it open.
        var context = Review(Closed("Bedroom", 2)).CoupledAlternatives!.Context;
        var closure = new ForecastPlannedClosure(1, "", [Inside], [], [], Closed("Bedroom", 2))
            { Placed = [new("wall", ["wall/finish", "wall/finish/after/eat"]), new("door", ["door/finish"])] };
        context = context with { Closures = [closure] };
        var closed = Assert.Single(ForecastRoofConsequences.PlannedClosedRooms(context,
            new Dictionary<string, double> { ["wall/finish/after/eat"] = 50, ["door/finish"] = 30 }));
        Assert.Equal(50, closed.ClosedAt);
        Assert.Empty(ForecastRoofConsequences.PlannedClosedRooms(context, new Dictionary<string, double> { ["wall/finish"] = 50 }));
        // With a planned ring site too, the room waits for it.
        context = context with { Closures = [closure with { Ring = [Wall] }] };
        Assert.Equal(60, Assert.Single(ForecastRoofConsequences.PlannedClosedRooms(context, new Dictionary<string, double>
        {
            ["wall/finish"] = 50, ["door/finish"] = 30, [context.PlannedSites.Single(s => s.Site == Wall).Finishes[0]] = 60,
        })).ClosedAt);
    }

    [Fact]
    public void ABenchInAnUnclosedPlannedRoomGetsAFasterClosureConditionalVariant()
    {
        var closure = new ForecastPlannedClosure(1, "WoodLog", [Inside], [Wall, Door], [],
            PlannedRoomPredictionTests.Closed("1", "WoodLog", "Workshop", 10d, 1, 4, 4, false)
            with { Benches = [new("bench", true), new("indoor-bench", false)] });
        var realized = new[]
        {
            new ForecastPlannedSite(Wall, 1, "WoodLog", ["wall/deliver", "wall/finish"], ["wall/finish"]),
            new ForecastPlannedSite(Door, 1, "WoodLog", ["door/finish"], ["door/finish"]),
        };
        var tasks = new List<ForecastActivity> { new("cook", "cell/1/1", "cell/1/1", ["fetch"]), new("craft", "cell/1/1", "cell/1/1", []) };
        var options = new List<ForecastActivityOption>
        {
            new("cook", "pawn", 1000d, 0d) { Equipment = ["bench"] },
            new("craft", "pawn", 1000d, 0d) { Equipment = ["indoor-bench"] },
        };
        var roots = new[] { new ForecastCommitment("cook-root", []) { ActivityCompletions = ["cook"] }, new ForecastCommitment("craft-root", []) { ActivityCompletions = ["craft"] } };
        var alternatives = roots.ToList();
        ForecastRoofConsequences.AddClosedBenchVariants([closure], roots, realized, tasks, options, new(), alternatives, new Dictionary<string, (int, string)>());

        var pricedTasks = tasks.Where(t => !t.Id.Contains("/closed/", StringComparison.Ordinal)).ToList();
        var pricedOptions = options.Where(o => !o.Activity.Contains("/closed/", StringComparison.Ordinal)).ToList();
        var pricedRoots = roots.ToList();
        ForecastRoofConsequences.AddClosedBenchVariants([closure], roots, realized, pricedTasks, pricedOptions, new(), pricedRoots,
            new Dictionary<string, (int, string)>(), new HashSet<int> { 1 });
        Assert.DoesNotContain(pricedTasks, t => t.Id.Contains("/closed/", StringComparison.Ordinal));
        Assert.Equal(roots.Length, pricedRoots.Count); // #3801 books its speed consequence only in Investment.

        var variant = Assert.Single(tasks, t => t.Id == "cook/closed/1/WoodLog");
        Assert.Equal(["fetch", "wall/finish", "door/finish"], variant.Prerequisites);
        Assert.Equal(800, Assert.Single(options, o => o.Activity == variant.Id).WorkTicks, 6);
        Assert.Equal(["cook"], Assert.Single(alternatives, a => a.Id == "cook-root/closed/1/WoodLog").ActivityCompletions.Select(_ => "cook"));
        // A bench already indoors, or a room still outdoors once closed (300 open roof cells), gets no variant.
        Assert.DoesNotContain(tasks, t => t.Id.StartsWith("craft/closed", StringComparison.Ordinal));
        var huge = closure with { Prediction = closure.Prediction with { Cells = 400, OpenRoofCells = 300 } };
        var before = tasks.Count;
        ForecastRoofConsequences.AddClosedBenchVariants([huge], roots, realized, tasks, options, new(), alternatives, new Dictionary<string, (int, string)>());
        Assert.Equal(before, tasks.Count);
    }
}
