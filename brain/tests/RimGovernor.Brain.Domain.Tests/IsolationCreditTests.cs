using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3844: a planned isolation room stands out of an agony pulse's reach of every other room, and is credited with the first
/// pulse a held creepjoiner would give every other colonist. The catalog has two drawable downsides of weight 1 and a 30-day
/// MTB, one the agony pulse, and one that adds a trait (shown from arrival). The pulse (AgonyPulse's game values) rises at
/// 0.1~0.15 a second from 0.001 through pain 1 to pain 0.25 at 0.95, and lasts 2000~4000 seconds.
/// </summary>
public sealed class IsolationCreditTests
{
    private const double Day = GameTime.TicksPerDay, Mtb = 30, Horizon = 10;

    private static CreepJoinerDownsideRow Downside(string def, bool agony = false, bool trait = false, double mtb = Mtb) => new()
    {
        Def = def, Weight = 1, CanOccurRandomly = true, AddsTrait = trait, TriggerMtbDays = mtb, Response = agony ? CreepJoinerResponse.PsychicAgony : CreepJoinerResponse.None,
    };

    private static readonly AgonyPulse Agony = new()
    {
        Range = 14.9, MaxSeverity = 1, SeverityPerSecond = (0.1, 0.15), DisappearsTicks = (2000 * 60, 4000 * 60),
        Table = new HediffPenaltyTable
        {
            Known = true, Hediff = "AgonyPulse", InitialSeverity = 0.001, Stages = [new PenaltyStage(0, 1, []), new PenaltyStage(0.95, 0.25, [])],
        },
    };

    private static CreepJoinerDownsideTable Table(bool agony = true) => new()
    {
        Downsides = [Downside("Aggressive"), Downside("PsychicAgony", agony), Downside("Disturbing", trait: true)],
        Agony = agony ? Agony : null,
    };

    private static ForecastCoupledContext Context(CreepJoinerForecast creepJoiners) =>
        WithCreepJoiner(ForecastValueTests.Context(ForecastValueTests.HarmPawn())) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.IsolationRoom },
            CreepJoiners = creepJoiners,
            InvestmentHorizonDays = Horizon,
        };

    /// <summary>The context's colonists: a colonist who can be pulsed and the creepjoiner "cj".</summary>
    private static ForecastCoupledContext WithCreepJoiner(ForecastCoupledContext context) =>
        context with { Needs = context.Needs with { Pawns = [ForecastValueTests.HarmPawn(), ForecastValueTests.HarmPawn() with { Pawn = "cj" }] } };

    private static CreepJoinerForecast Held(double days = 2, bool agony = true) => new() { Unread = "", Held = [new HeldCreepJoiner("cj", days)], Table = Table(agony) };

    private static ForecastCoupledOutcome Outcome() => new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), []);

    private static readonly ForecastRoomService Room = new(1, "", 0);

    // A 30-day MTB checked four times a day: (1 − 1/120) a check, as a continuous rate.
    private static double Left(double days) => Math.Exp(Math.Log(1 - (0.25 / Mtb)) / 0.25 * days);

    [Fact]
    public void AnUnshownDownsideIsDrawnByWeightAndFiresAtItsMtb()
    {
        var agony = Downside("PsychicAgony", agony: true);
        Assert.Equal(Left(3), ForecastIsolation.Survival(agony, 3), 12);
        Assert.Equal(1, ForecastIsolation.Survival(agony with { TriggerMtbDays = 0 }, 3));
        // A timer of exactly 5 days fires within the check after it: certain past 5.25 days, not before 5.
        var timer = agony with { TriggerMtbDays = 0, TriggersAfterDays = (5, 5) };
        Assert.Equal((1, 0.5, 0), (ForecastIsolation.Survival(timer, 4.9), ForecastIsolation.Survival(timer, 5.125), ForecastIsolation.Survival(timer, 6)));
        // The MTB opens only past its gate: a gate of 4 days leaves it unfired at 3 and fired at its rate from 4.
        var gated = agony with { TriggerMinDays = (4, 4) };
        Assert.Equal((1, Left(2)), (ForecastIsolation.Survival(gated, 3), ForecastIsolation.Survival(gated, 6)), new Tolerance());

        // Two hidden downsides alike, the trait one shown: the agony one is half the posterior, and fires in the window at its MTB.
        var chance = ForecastIsolation.FirstPulseChance(Table(), 2, 12, 2);
        Assert.Equal((Left(2) - Left(12)) / (2 * Left(2)), chance, 12);
        Assert.Equal(0, ForecastIsolation.FirstPulseChance(Table(agony: false), 2, 12, 2));
        Assert.Throws<RequiredFactException>(() => ForecastIsolation.FirstPulseChance(Table() with { Unmirrored = "a form requires PsychicAgony" }, 2, 12, 2));
        var gatedOnPoints = Table() with { Downsides = [Downside("Aggressive") with { MinCombatPoints = 100 }] };
        Assert.Throws<RequiredFactException>(() => ForecastIsolation.FirstPulseChance(gatedOnPoints, 2, 12, 2));
    }

    [Fact]
    public void APulseDownsAColonistUntilItsPainFallsThenCostsWhatThePainTakes()
    {
        var perSeverity = Math.Log(1.5) / 0.05 * 60;
        var downed = (0.95 - 0.001) * perSeverity;
        var length = 3000 * 60d;
        var pain = 1 - HarmConsequences.PainConsciousness(1, 0.25);
        Assert.Equal((downed + ((length - downed) * pain)) / Day, ForecastIsolation.PulseLoss(ForecastValueTests.HarmPawn(), Agony), 12);    }

    [Fact]
    public void AHeldCreepjoinerWhoseDownsideMayPulseIsCreditedTheOtherColonistsFirstPulse()
    {
        var context = Context(Held());
        var gain = Assert.Single(ForecastIsolation.IsolationCredit(Room, Outcome(), [], context, Day));
        var expected = ForecastIsolation.FirstPulseChance(Table(), 2, 2 + Horizon, 2) * ForecastIsolation.PulseLoss(ForecastValueTests.HarmPawn(), Agony);
        Assert.True(expected > 0);
        Assert.Equal(("cj", "", 0d), (gain.Subject, gain.Pawn, gain.PerDay));
        Assert.Equal(expected, gain.Once, 12);
        Assert.Equal(ForecastIsolation.NotModelled, ForecastIsolation.Findings(Room, Outcome(), [], context, Day));
    }

    [Fact]
    public void NoAgonyDownsideOrNoHeldCreepjoinerEarnsANamedZero()
    {
        var noAgony = Context(Held(agony: false));
        Assert.Empty(ForecastIsolation.IsolationCredit(Room, Outcome(), [], noAgony, Day));
        Assert.Equal([ForecastIsolation.NoAgony], ForecastIsolation.Findings(Room, Outcome(), [], noAgony, Day));
        var none = Context(new() { Unread = "", Held = [], Table = Table() });
        Assert.Empty(ForecastIsolation.IsolationCredit(Room, Outcome(), [], none, Day));
        Assert.Equal([ForecastIsolation.NoneHeld], ForecastIsolation.Findings(Room, Outcome(), [], none, Day));
        // Unread creepjoiners, or a pulsing downside with no pulse read, are required facts.
        Assert.Throws<RequiredFactException>(() => ForecastIsolation.IsolationCredit(Room, Outcome(), [], Context(new()), Day));
        Assert.Throws<RequiredFactException>(() => ForecastIsolation.IsolationCredit(Room, Outcome(), [], Context(Held() with { Table = Table() with { Agony = null } }), Day));
    }

    [Fact]
    public void TheCreditRidesTheLedgerFromTheRoomsServiceDay()
    {
        // PlannedRoomCreditTests' room 1 serves from day three of a ten-day horizon.
        var context = WithCreepJoiner(PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped)) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.IsolationRoom, [2] = PlannedRole.Workshop },
            CreepJoiners = Held(),
        };
        var ledger = ForecastValue.Of(PlannedRoomCreditTests.Outcome(context), context, Day);
        var row = Assert.Single(ledger.InvestmentContributions, r => r.Key.StartsWith(Wire.Name(PlannedRole.IsolationRoom) + "/", StringComparison.Ordinal));
        Assert.Equal(PlannedRoomCredits.Key(PlannedRole.IsolationRoom, 1, "cj"), row.Key);
        var once = ForecastIsolation.FirstPulseChance(Table(), 2 + 3, 2 + context.InvestmentHorizonDays, 2) * ForecastIsolation.PulseLoss(ForecastValueTests.HarmPawn(), Agony);
        Assert.Equal(-once, row.Value, 9);
        Assert.All(ForecastIsolation.NotModelled, f => Assert.Contains(f, ledger.Findings));
    }

    [Fact]
    public void TheLayoutKeepsEveryOtherRoomOutOfAPulsesReach()
    {
        // Interiors 15 cells apart on one axis stand out of reach; 14 do not; a diagonal 11 by 11 does not (242 > 222.01 does).
        var room = new Policy.Rectangle(0, 0, 3, 3);
        Assert.True(IsolationRoomRules.ApartFromIsolation(room, new(17, 0, 3, 3)));
        Assert.False(IsolationRoomRules.ApartFromIsolation(room, new(16, 0, 3, 3)));
        Assert.True(IsolationRoomRules.ApartFromIsolation(room, new(13, 13, 3, 3)));
        Assert.False(IsolationRoomRules.ApartFromIsolation(room, new(12, 12, 3, 3)));
        Assert.True(IsolationRoomRules.KeptApart(PlannedRole.IsolationRoom, PlannedRole.Bedroom));
        Assert.True(IsolationRoomRules.KeptApart(PlannedRole.None, PlannedRole.IsolationRoom));
        Assert.False(IsolationRoomRules.KeptApart(PlannedRole.IsolationRoom, PlannedRole.Circulation));
        Assert.False(IsolationRoomRules.KeptApart(PlannedRole.Bedroom, PlannedRole.Kitchen));

        // The embedding places the isolation room out of reach of the other room.
        var emb = Embedding.Embed(Input(40));
        var (a, okA) = emb.Block(0);
        var (b, okB) = emb.Block(1);
        Assert.True(okA && okB);
        Assert.True(IsolationRoomRules.ApartFromIsolation(a, b), $"{a} {b}");
        // A map too small to hold them apart has no isolation room: the other room alone is placed.
        var small = Embedding.Embed(Input(16));
        Assert.False(small.Block(0).Ok && small.Block(1).Ok);
    }

    private static EmbedInput Input(int side)
    {
        var survey = new MapSurvey
        {
            Bounds = new Bounds(side, side),
            Cells = [.. Enumerable.Range(0, side).SelectMany(x => Enumerable.Range(0, side).Select(z => new SurveyCell { Cell = new Cell(x, z), Walkable = true, Footing = Footing.Firm, Fertility = 1 }))],
        };
        return new()
        {
            Groups = [new RoomGroup { Id = 0, Area = 6, Footprint = 3, Clearance = 3, Planned = PlannedRole.IsolationRoom }, new RoomGroup { Id = 1, Area = 12, Footprint = 6, Clearance = 6 }],
            Survey = survey,
            RoofDistance = 6,
            Zones = [new LayoutZone { Kind = LayoutZoningRules.ZoneCore, Runs = [.. survey.Cells.Select(c => new RowRun(c.Cell.Z, c.Cell.X, 1))] }],
        };
    }

    [Fact]
    public void TheRoundsReadTheHeldCreepjoinersJoinTickAndTheCatalogsPulse()
    {
        // The production path (#3822): the native pawn row's tracker gives joinedTick; the catalog reads the downside defs,
        // the worker's Range and DisappearSeconds by name, and AgonyPulse's stages and rate.
        var catalog = new DefinitionCatalog([
            new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" },
            new Verse.CreepJoinerDownsideDef { defName = "PsychicAgony", workerType = typeof(RimWorld.CreepJoinerWorker_PsychicAgony), triggerMtbDays = (float)Mtb },
            new Verse.HediffDef
            {
                defName = "AgonyPulse", initialSeverity = 0.001f, maxSeverity = 1,
                stages = [new Verse.HediffStage { minSeverity = 0, painOffset = 1 }, new Verse.HediffStage { minSeverity = 0.95f, painOffset = 0.25f }],
                comps = [new Verse.HediffCompProperties_SeverityPerSecond { severityPerSecondRange = new Verse.FloatRange(0.1f, 0.15f) }],
            },
        ], DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));
        var table = catalog.CreepJoinerDownsideTable();
        var row = Assert.Single(table.Downsides);
        Assert.True(row.PsychicAgony);
        Assert.Equal(Mtb, row.TriggerMtbDays);
        var pulse = table.Agony!;
        Assert.Equal((Agony.Range, Agony.MaxSeverity, Agony.SeverityPerSecond, Agony.DisappearsTicks), (pulse.Range, pulse.MaxSeverity, pulse.SeverityPerSecond, pulse.DisappearsTicks));
        Assert.Equal(ForecastIsolation.PulseLoss(ForecastValueTests.HarmPawn(), Agony), ForecastIsolation.PulseLoss(ForecastValueTests.HarmPawn(), pulse), 9);

        var tracker = new O.PawnAnomalyState.Active(new O.PawnAnomaly
        {
            Entity = false, Mutant = false, MinContainmentStrength = 0, HiddenFromPlayer = false, PsychicRitualInvoker = false, MeleeOnly = false,
            Creepjoiner = new O.CreepJoinerState { DownsideTriggered = false, JoinedTick = (int)Day, CombatPower = 55 }, Held = null, Study = null,
        });
        var frame = new RimGovernor.Brain.Bridge.Contracts.RoundsFrame
        {
            Pawns = new O.PawnSnapshot { Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 0 }, Pawns = [PawnFixtures.Row("cj") with { Anomaly = tracker }, PawnFixtures.Row("pawn")] },
        };
        var projection = ColonyFactsCoreTests.Projection() with
        {
            Identity = new() { Tick = new((long)(3.5 * Day)) },
            Isolation = new IsolationPlanning { Pawns = Known(EquatableArray.From([new PawnId("cj")])) },
        };
        var forecast = RoundsCreepJoiner.CreepJoinerForecastOf(projection, frame, catalog);
        Assert.Equal("", forecast.Unread);
        Assert.Equal([new HeldCreepJoiner("cj", 2.5)], forecast.Held);
        Assert.Same(table, forecast.Table);
        Assert.Single(ForecastIsolation.IsolationCredit(Room, Outcome(), [], Context(forecast), Day));

        // A fresh colony holds no creepjoiner apart: a named zero, nothing thrown.
        var fresh = RoundsCreepJoiner.CreepJoinerForecastOf(projection with { Isolation = new IsolationPlanning { Pawns = Known(EquatableArray.From(Array.Empty<PawnId>())) } }, frame, catalog);
        Assert.Empty(ForecastIsolation.IsolationCredit(Room, Outcome(), [], Context(fresh), Day));
        Assert.Equal([ForecastIsolation.NoneHeld], ForecastIsolation.Findings(Room, Outcome(), [], Context(fresh), Day));
        // An unread census of the held apart is unread.
        Assert.NotEqual("", RoundsCreepJoiner.CreepJoinerForecastOf(projection with { Isolation = new IsolationPlanning() }, frame, catalog).Unread);
    }

    private sealed class Tolerance : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-12 && Math.Abs(x.Item2 - y.Item2) < 1e-12;

        public int GetHashCode((double, double) obj) => 0;
    }
}
