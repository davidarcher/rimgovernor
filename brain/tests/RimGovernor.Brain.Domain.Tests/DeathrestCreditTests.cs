using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;
using Obs = RimGovernor.Brain.Observation;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

// #3810 (epic #3650 P7): a planned deathrest chamber is credited, per deathrester, the share of days it no longer spends
// deathresting (Need_Deathrest: FallPerDay / (GainPerDayDeathresting × efficiency + FallPerDay)) as capability-days, plus the
// DeathrestChamber thought its predicted Impressiveness gives between deathrests through GainPerDay.
public sealed class DeathrestCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private static readonly Cell Inside = new(1, 1);

    /// <summary>The game's defs: Need_Deathrest's rates, the casket (×1.1) and accelerator (×1.2, two at most) and Impressiveness's score stages.</summary>
    private static readonly DeathrestDefsRead Defs = new(1 / 30d, 0.2,
        [new("DeathrestAccelerator", 1.2, 2, true, false), new("DeathrestCasket", 1.1, 1, false, true), new("HemogenAmplifier", 1, 1, true, false)],
        [-1e9, 20, 30, 40, 50, 65, 85, 120, 170]);

    /// <summary>The DeathrestChamber thought's stages: outdoors, awful, dull and mediocre (none), decent, slightly impressive, impressive, very impressive.</summary>
    private static readonly double[] ChamberOffsets = [-4, -4, 0, 0, 2, 3, 4, 5];

    private static NeedsForecastPawn Deathrester(string id = "pawn", int capacity = 1, double efficiency = 1, int chamberStage = -1) => ForecastValueTests.Pawn() with
    {
        Pawn = id,
        Deathrest = new(capacity, efficiency, chamberStage),
        ThoughtStages = EquatableArray.From(ChamberOffsets.Select((o, i) => new NeedThoughtStage("DeathrestChamber", ForecastDeathrest.ChamberThoughtRole, i, o, NeedsFixtures.Situational))),
    };

    private static readonly EquatableArray<DeathrestPiece> CasketAndAccelerator = [new("DeathrestCasket", 1), new("DeathrestAccelerator", 1)];

    private static ForecastCoupledContext Context(NeedsForecastPawn pawn, EquatableArray<DeathrestPiece> furniture, double impressiveness = 35, DeathrestDefsRead? defs = null)
        => ForecastValueTests.Context(pawn) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [pawn], Deathrest = defs ?? Defs },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.DeathrestChamber },
            Closures = [new(1, "WoodLog", [Inside], ["planned/room1/wall/WoodLog"], [],
                PlannedRoomPredictionTests.Closed("1", "WoodLog", "DeathrestChamber", impressiveness, 2, 12, 0, false))],
            DeathrestFurniture = furniture,
            InvestmentHorizonDays = Horizon,
        };

    /// <summary>A day in the mildest break tier unshifted, and none once shifted by <paramref name="shift"/>; the room serves at <paramref name="servedAt"/>.</summary>
    private static ForecastCoupledOutcome Serving(double servedAt, double shift = 0) =>
        new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], default, default, new(new("pawn", "", new ForecastCapacityWindows.Unread(), 0), []), [])
            {
                Mood = MoodResults.Flat(0d, [Day, 0, 0], [new(shift, [0, 0, 0])]),
            },
        }, [])
        { ServedRooms = [new(1, "WoodLog", servedAt)] };

    private static ForecastInvestmentContribution[] Rows(ForecastCoupledContext context, ForecastCoupledOutcome outcome)
        => [.. ForecastValue.Of(outcome, context, Day).InvestmentContributions.Where(r => r.Key.StartsWith("deathrest-chamber/", StringComparison.Ordinal))];

    private static double Share(double efficiency) => Defs.FallPerDay / (Defs.GainPerDay * efficiency + Defs.FallPerDay);

    [Fact]
    public void ADeathresterWithoutACasketIsCreditedTheWorkDaysACasketAndOneAcceleratorRecover()
    {
        // A mediocre chamber gives no DeathrestChamber thought, and the deathrester has none now: the credit is the work days alone.
        var context = Context(Deathrester(), CasketAndAccelerator);
        Assert.True(context.Priced(1));
        var recovered = Share(1) - Share(1.1 * 1.2);
        Assert.Equal(1 / 7d - (1 / 30d) / (0.2 * 1.32 + 1 / 30d), recovered, 12);
        var row = Assert.Single(Rows(context, Serving(Day / 2)));
        Assert.Equal(("pawn", "deathrest-chamber/1/pawn", Horizon - 0.5), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-recovered * (Horizon - 0.5), row.Value, 12);
    }

    [Fact]
    public void NoDeathresterEarnsNothing()
    {
        var context = Context(ForecastValueTests.Pawn(), CasketAndAccelerator);
        Assert.Empty(Rows(context, Serving(0)));
        // Without Biotech the read carries no deathrest defs and no pawn deathrests: still nothing, and no throw.
        Assert.Empty(Rows(context with { Needs = context.Needs with { Deathrest = null } }, Serving(0)));
    }

    [Fact]
    public void TheChamberThoughtItsImpressivenessGivesReplacesTheOneTheLastDeathrestLeftOverTheAwakeShare()
    {
        // Last deathrest in an awful room (-4); the planned chamber is slightly impressive (score stage 4, chamber stage 5: +3).
        var after = Share(1.1 * 1.2);
        var mood = 3 * (1 - after) - -4 * (1 - Share(1));
        var context = Context(Deathrester(chamberStage: 1), CasketAndAccelerator, impressiveness: 55);
        var outcome = Serving(0, mood / 100);
        Assert.Equal([mood / 100], PlannedRoomCredits.BreakShifts(context)["pawn"]);
        var perDay = Share(1) - after + ForecastHousing.GainPerDay(mood, outcome.Needs["pawn"].Mood, context.MoodBreaks, context.Value.WellbeingWeight, 1)!.Value;
        var row = Assert.Single(Rows(context, outcome));
        Assert.Equal(-perDay * Horizon, row.Value, 12);
    }

    [Fact]
    public void AnAsceticGetsNoChamberThoughtAndADeathresterWhoAlreadyHasTheSameBuildingsBoundGainsNothing()
    {
        var ascetic = Deathrester() with { Ascetic = true };
        Assert.Equal(-(Share(1) - Share(1.32)) * Horizon, Assert.Single(Rows(Context(ascetic, CasketAndAccelerator, impressiveness: 55), Serving(0))).Value, 12);
        Assert.Empty(Rows(Context(Deathrester(efficiency: 1.32), CasketAndAccelerator), Serving(0)));
    }

    [Fact]
    public void EachDeathresterTakesOneCasketAndBindsAcceleratorsUpToItsCapacityAndTheStackLimit()
    {
        // Capacity 3 binds the two accelerators the stack limit allows and the amplifier (×1); the second pawn finds no casket left.
        var a = Deathrester("a", capacity: 3);
        var b = Deathrester("b", capacity: 3);
        var context = ForecastValueTests.Context(a) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [a, b], Deathrest = Defs },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.DeathrestChamber },
            Closures = [new(1, "WoodLog", [Inside], ["planned/room1/wall/WoodLog"], [], PlannedRoomPredictionTests.Closed("1", "WoodLog", "DeathrestChamber", 35, 2, 12, 0, false))],
            DeathrestFurniture = [new("DeathrestCasket", 1), new("DeathrestAccelerator", 3), new("HemogenAmplifier", 1)],
            InvestmentHorizonDays = Horizon,
        };
        var gains = ForecastDeathrest.ChamberCredit(new(1, "WoodLog", 0), Serving(0), [], context, Day);
        var gain = Assert.Single(gains);
        Assert.Equal("a", gain.Pawn);
        Assert.Equal(Share(1) - Share(1.1 * 1.2 * 1.2), gain.PerDay, 12);
    }

    [Fact]
    public void ADeathresterWithoutTheDeathrestDefsOrAChamberStageRowIsARequiredFact()
    {
        var context = Context(Deathrester(), CasketAndAccelerator);
        Assert.Throws<RequiredFactException>(() => ForecastDeathrest.ChamberCredit(new(1, "WoodLog", 0), Serving(0), [], context with { Needs = context.Needs with { Deathrest = null } }, Day));
        var unread = Deathrester(chamberStage: 1) with { ThoughtStages = [] };
        Assert.Throws<RequiredFactException>(() => ForecastDeathrest.ChamberCredit(new(1, "WoodLog", 0), Serving(0), [], Context(unread, CasketAndAccelerator), Day));
    }

    // The production path: the staging's chamber need (ChildRoomsRules.ChildRoomNeeds over the work pawns' Biotech facts) resolved
    // against the catalog's deathrest buildings (ChildRoomsRules.DeathrestFurniture, as RoundsSleepingUpkeepPlanner.DeathrestFurnitureOf).
    private static readonly IReadOnlyList<FurnitureDefinition> Catalog =
    [
        new() { Name = "DeathrestCasket", Available = Known(true), Size = Known(new P.Bounds(1, 2)), Roles = [FurnitureRole.DeathrestCasket.Value] },
        new() { Name = "DeathrestAccelerator", Available = Known(true), Size = Known(new P.Bounds(1, 2)), Roles = [FurnitureRole.DeathrestAccelerator.Value] },
        new() { Name = "HemogenAmplifier", Available = Known(true), Size = Known(new P.Bounds(1, 2)), Roles = [FurnitureRole.DeathrestAccelerator.Value] },
    ];

    private static WorkPawn Worker(P.PawnDeathrest? deathrest) => PawnFixtures.Worker("pawn") with
    {
        Biotech = new PawnBiotechStatus.Active(Biotech(deathrest, RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Adult)),
    };

    private static P.PawnBiotech Biotech(P.PawnDeathrest? deathrest, RimGovernor.Contracts.Wire.Defs.DevelopmentalStage stage) =>
        new() { DevelopmentalStage = stage, Genes = [], Mechanitor = null, Mech = null, Deathrest = deathrest };

    private static EquatableArray<DeathrestPiece> Staged(DeathrestDefsRead? defs, params WorkPawn[] pawns) =>
        ChildRoomsRules.DeathrestFurniture(ChildRoomsRules.ChildRoomNeeds(pawns, defs), Catalog);

    [Fact]
    public void AFreshColonysDefaultDeathresterIsStagedACasketAndOneAcceleratorAndCreditedThem()
    {
        // #3842: the casket does not count toward capacity, so a capacity-1 deathrester binds one accelerator beside it. The
        // amplifier (factor 1) raises no efficiency and is not staged.
        var furniture = Staged(Defs, Worker(new P.PawnDeathrest { Deathresting = false, Capacity = 1 }));
        Assert.Equal([new DeathrestPiece("DeathrestCasket", 1), new DeathrestPiece("DeathrestAccelerator", 1)], furniture.Items);
        var row = Assert.Single(Rows(Context(Deathrester(capacity: 1), furniture), Serving(0)));
        Assert.Equal(-(Share(1) - Share(1.32)) * Horizon, row.Value, 12);
    }

    [Fact]
    public void TheStagingFurnishesEachDeathresterTheAcceleratorsItsStackLimitAllows()
    {
        // Capacity 3 binds two accelerators (the stack limit); two such deathresters get two caskets and four accelerators,
        // and the credit binds them all.
        var a = Worker(new P.PawnDeathrest { Deathresting = false, Capacity = 3 });
        var furniture = Staged(Defs, a, a with { Id = new("b") });
        Assert.Equal([new DeathrestPiece("DeathrestCasket", 2), new DeathrestPiece("DeathrestAccelerator", 4)], furniture.Items);
        var context = Context(Deathrester("a", capacity: 3), furniture) with { Needs = NeedsFixtures.Empty with { Pawns = [Deathrester("a", capacity: 3), Deathrester("b", capacity: 3)], Deathrest = Defs } };
        var gains = ForecastDeathrest.ChamberCredit(new(1, "WoodLog", 0), Serving(0), [], context, Day);
        Assert.Equal(["a", "b"], gains.Select(g => g.Pawn));
        Assert.All(gains, g => Assert.Equal(Share(1) - Share(1.1 * 1.2 * 1.2), g.PerDay, 12));
    }

    [Fact]
    public void AFreshColonyWithoutADeathresterOrBiotechOwesNoChamberAndCreditsNothing()
    {
        Assert.Empty(Staged(Defs, Worker(null)));
        var furniture = Staged(null, Worker(null));
        Assert.Empty(furniture);
        var context = Context(ForecastValueTests.Pawn(), furniture) with { Needs = NeedsFixtures.Empty with { Pawns = [ForecastValueTests.Pawn()] } };
        Assert.Empty(Rows(context, Serving(0)));
    }

    [Fact]
    public void PawnsWithoutADeathrestGeneStageTheirChildRooms()
    {
        // Pawns with no deathrest gene, or no Biotech at all, never throw, and their nursery and classroom stage.
        static WorkPawn Child(string id, RimGovernor.Contracts.Wire.Defs.DevelopmentalStage stage) =>
            PawnFixtures.Worker(id) with { Biotech = new PawnBiotechStatus.Active(Biotech(null, stage)) };
        var needs = ChildRoomsRules.ChildRoomNeeds([
            Child("baby", RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Baby),
            Child("child", RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Child),
            PawnFixtures.Worker("plain")], Defs);
        Assert.Equal([RoomRole.Nursery, RoomRole.Classroom], needs.Select(n => n.Role));
    }

    [Fact]
    public void AFreshColonysDeathresterIsStagedFromTheReadsTheRoundDecodes()
    {
        // The production path (#3822): the pawn row's wire Biotech block lifted by PawnBiotech.Of, the needs read's deathrest
        // defs on ForecastObserved and the catalog's role rows, through DeathrestFurnitureOf as the round sets the credit's context.
        Obs.PlanningDefinition Row(string name, FurnitureRole role) =>
            new() { Name = name, Available = Known(true), Size = Known(new P.Bounds(1, 2)), RoomRoles = [role.Value] };
        var worker = PawnFixtures.Worker("pawn") with { Biotech = new PawnBiotechStatus.Active(RimGovernor.Brain.Policy.PawnBiotech.Of(new O.PawnBiotech { DevelopmentalStage = RimGovernor.Contracts.Wire.Defs.DevelopmentalStage.Adult, Genes = [], Mechanitor = null, Mech = null, Deathrest = new O.PawnDeathrest { Deathresting = false, Capacity = 1 } })) };
        var colony = ColonyFactsCoreTests.Projection() with
        {
            WorkPawns = Known(EquatableArray.From([worker])),
            Facts = new() { ForecastObserved = new() { Needs = NeedsFixtures.Empty with { Pawns = [Deathrester()], Deathrest = Defs } } },
            Definitions = [Row("DeathrestCasket", FurnitureRole.DeathrestCasket), Row("DeathrestAccelerator", FurnitureRole.DeathrestAccelerator), Row("HemogenAmplifier", FurnitureRole.DeathrestAccelerator)],
        };
        var furniture = RoundsSleepingUpkeepPlanner.DeathrestFurnitureOf(colony);
        Assert.Equal([new DeathrestPiece("DeathrestCasket", 1), new DeathrestPiece("DeathrestAccelerator", 1)], furniture.Items);
        Assert.Equal(-(Share(1) - Share(1.32)) * Horizon, Assert.Single(Rows(Context(Deathrester(), furniture), Serving(0))).Value, 12);
        // Without Biotech no pawn row carries a block and the needs read no defs: no chamber is owed.
        Assert.Empty(RoundsSleepingUpkeepPlanner.DeathrestFurnitureOf(colony with { WorkPawns = Known(EquatableArray.From([PawnFixtures.Worker("pawn")])), Facts = new() }));
    }

    [Fact]
    public void ADeathresterWhoseDeathrestDefsAreUnreadIsARequiredFactForTheStaging()
    {
        Assert.Throws<RequiredFactException>(() => Staged(null, Worker(new P.PawnDeathrest { Deathresting = false, Capacity = 1 })));
    }
}
