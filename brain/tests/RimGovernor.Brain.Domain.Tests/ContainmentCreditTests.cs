using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3811: a planned containment cell is credited with what it changes for the entity it holds, and the harm term counts the
/// held entities' escapes. The entity needs 50 containment strength; its platform now gives 30 (20 short: the escape interval
/// ×0.05, the study ×0.5) and the planned cell 50 (×1 each). It escapes once per 30 days at ×1, a fight of 1000 combat power
/// (the exposure table's 1–2 cell at 1000 defense), and gives 2 knowledge a day at ×1 / 5, each point worth 3.
/// </summary>
public sealed class ContainmentCreditTests
{
    private const double Day = GameTime.TicksPerDay, Need = 50, Now = 30, Planned = 50, MtbDays = 30, Knowledge = 2, Price = 3;

    private static readonly ContainmentCurves Curves = new()
    {
        Escape = [new(-0.001, 0.05), new(0, 1), new(100, 3), new(200, 8)],
        Study = [new(-0.001, 0.5), new(0, 1), new(100, 1.3), new(200, 1.5)],
        Captivity = [new(15, 1), new(60, 1.2), new(120, 1.5), new(240, 2)],
    };

    private static ContainedEntity Entity(string id = "entity1", bool held = true) => new(id, Need,
        new EntityEscape(MtbDays, 1, 0, 1, 1000, held), new StudyKnowledge(Knowledge, "Basic", (int)Day), held ? new EntityPlatform(new(5, 5), Now) : null);

    private static ForecastCoupledContext Context(EquatableArray<ContainedEntity> entities, string held = "entity1", string unread = "", ContainmentCurves? curves = null) =>
        ForecastValueTests.Context(ForecastValueTests.HarmPawn(), ForecastValueTests.HarmTables(),
            ForecastValueTests.RaidDay(raidsPerDay: 0) with { Containment = new() { Entities = entities, Unread = unread, Curves = curves ?? Curves } },
            new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = ForecastValueTests.Cloth }) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.ContainmentCell },
            ContainmentCells = [new PlannedContainmentCell(1, Planned, held)],
            Investment = new Dictionary<string, double> { [ProjectGraph.KnowledgeKey("Basic")] = Price },
        };

    private static ForecastCoupledOutcome Outcome() =>
        new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult> { ["pawn"] = ForecastValueTests.Timeline(ForecastValueTests.HarmPawn()) }, []);

    private static readonly ForecastRoomService Cell = new(1, "", 0);

    [Fact]
    public void AHeldEntityShortOfItsNeedIsCreditedTheEscapesAndStudyAPlannedCellMeetingItGives()
    {
        var context = Context([Entity()]);
        var outcome = Outcome();
        var fight = ForecastValue.EscapeLoss(outcome, context, Day, 1000);
        Assert.True(fight > 0);
        // The escapes the cell stops (one per 1.5 days now, one per 30 in the cell) times one fight, plus the study it adds
        // (0.4 knowledge a day at ×0.5 now, at ×1 in the cell, each point worth 3).
        var avoided = (1 / (MtbDays * 0.05) - 1 / MtbDays) * fight;
        var study = Knowledge / ForecastContainment.StudiesPerKnowledge * (1 - 0.5) * Price;
        var gain = Assert.Single(ForecastContainment.CellCredit(Cell, outcome, [], context, Day));
        Assert.Equal(("entity1", ""), (gain.Subject, gain.Pawn));
        Assert.Equal(avoided + study, gain.PerDay, 9);
        Assert.Equal(ForecastContainment.NotModelled, ForecastContainment.CellFindings(Cell, outcome, [], context, Day));

        // The harm term counts the escapes the entity makes on its platform now: a fight each, over the horizon's day.
        var ledger = ForecastValue.Of(outcome, context, Day);
        var escape = ledger.HarmDetail!.Escape;
        Assert.Equal(1 / (MtbDays * 0.05), escape.EscapesPerDay, 12);
        Assert.Equal(escape.EscapesPerDay * fight, escape.Loss, 9);
        Assert.True(escape.DeathsPerDay >= 0);
        Assert.Contains("containment_escape_raid_calibration_assumed", ledger.HarmFindings);
    }

    [Fact]
    public void ASmallEntitysEscapeIsPricedFromTheCommittedTablesLowCellsAgainstAnArmedDefender()
    {
        // #3880: a 100-point entity against 1000 defense is a fight under a 0.5 ratio; the committed table prices it for a
        // gunner and a melee fighter, and the harm term carries the escapes.
        var small = Context([Entity() with { Escape = Entity().Escape with { CombatPower = 100 } }]);
        small = small with { Harm = small.Harm with { Exposure = RaidExposure.Load() } };
        foreach (var weapon in new[] { CreepJoinerHarmTests.Rifle, CreepJoinerHarmTests.Longsword })
        {
            var armed = small with { Outfits = new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = [.. ForecastValueTests.Cloth, weapon] } };
            var fight = ForecastValue.EscapeLoss(Outcome(), armed, Day, 100);
            Assert.True(fight > 0);
            Assert.Equal(1 / (MtbDays * 0.05) * fight, ForecastValue.Of(Outcome(), armed, Day).HarmDetail!.Escape.Loss, 9);
        }
    }

    [Fact]
    public void AnEscapeFightWithAnUnreadDefenderThrowsARequiredFact()
    {
        // #3835 (#3811's escape_unassessed): a defender whose outfit is unread leaves the fight unpriced, so it throws.
        var context = Context([Entity()]) with { Outfits = new Dictionary<string, EquatableArray<GearOption>>() };
        Assert.StartsWith("outfit_unobserved:", Assert.Throws<RequiredFactException>(() => ForecastValue.EscapeLoss(Outcome(), context, Day, 1000)).Fact, StringComparison.Ordinal);
        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(), context, Day));
    }

    [Fact]
    public void TheCellCreditRidesTheLedgerFromItsServiceDayWithWhatItLeavesOut()
    {
        // PlannedRoomCreditTests' room 1 serves from day three of a ten-day horizon; its outcome has no pawn, so an escape
        // fights no defender and costs nothing (#3835): the credit is the study alone.
        var context = PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.ContainmentCell, [2] = PlannedRole.Workshop },
            ContainmentCells = [new PlannedContainmentCell(1, Planned, "entity1")],
            Investment = new Dictionary<string, double> { [ProjectGraph.KnowledgeKey("Basic")] = Price },
        };
        context = context with { Harm = context.Harm with { Containment = new() { Entities = [Entity()], Curves = Curves } } };
        var ledger = ForecastValue.Of(PlannedRoomCreditTests.Outcome(context), context, Day);
        var row = Assert.Single(ledger.InvestmentContributions, r => r.Key.StartsWith(Wire.Name(PlannedRole.ContainmentCell) + "/", StringComparison.Ordinal));
        Assert.Equal((PlannedRoomCredits.Key(PlannedRole.ContainmentCell, 1, "entity1"), 7d), (row.Key, row.Units));
        Assert.Equal(-Knowledge / ForecastContainment.StudiesPerKnowledge * 0.5 * Price * 7, row.Value, 9);
        Assert.Equal(0, ForecastValue.EscapeLoss(PlannedRoomCreditTests.Outcome(context), context, Day, 1000));
        Assert.All(ForecastContainment.NotModelled, f => Assert.Contains(f, ledger.Findings));
        Assert.DoesNotContain(ledger.Findings, f => f.StartsWith("escape", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyCellTakesAWaitingEntityAndEarnsItsStudyLessItsEscapes()
    {
        var waiting = Entity("entity2", held: false);
        var context = Context([waiting], held: "");
        var outcome = Outcome();
        var fight = ForecastValue.EscapeLoss(outcome, context, Day, 1000);
        var gain = Assert.Single(ForecastContainment.CellCredit(Cell, outcome, [], context, Day));
        Assert.Equal("entity2", gain.Subject);
        Assert.Equal(Knowledge / ForecastContainment.StudiesPerKnowledge * Price - 1 / MtbDays * fight, gain.PerDay, 9);
        // A waiting entity escapes no platform, so the harm term counts no escape for it.
        Assert.Equal(new EscapeTerm(0, 0, 0), ForecastValue.Of(outcome, context, Day).HarmDetail!.Escape);
    }

    [Fact]
    public void ACellWithNoEntityEarnsNothingAndAColonyWithoutEntitiesNeedsNoCurves()
    {
        var context = Context([], held: "", curves: new ContainmentCurves { Reason = "never read" });
        Assert.Empty(ForecastContainment.CellCredit(Cell, Outcome(), [], context, Day));
        Assert.Empty(ForecastContainment.CellFindings(Cell, Outcome(), [], context, Day));
        Assert.Equal(new EscapeTerm(0, 0, 0), ForecastValue.Of(Outcome(), context, Day).HarmDetail!.Escape);
        // A second cell holding no one, with the entity in the first, earns nothing either.
        var two = Context([Entity()]) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.ContainmentCell, [2] = PlannedRole.ContainmentCell },
            ContainmentCells = [new PlannedContainmentCell(1, Planned, "entity1"), new PlannedContainmentCell(2, Planned, "")],
        };
        Assert.Empty(ForecastContainment.CellCredit(new(2, "", 0), Outcome(), [], two, Day));
    }

    [Fact]
    public void UnreadEntitiesCurvesOrACellPredictionAreRequiredFacts()
    {
        var unread = Context([], unread: "entity1: the held block failed");
        Assert.Equal("containment entities: entity1: the held block failed",
            Assert.Throws<RequiredFactException>(() => ForecastContainment.CellCredit(Cell, Outcome(), [], unread, Day)).Fact);
        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(), unread, Day));
        var curves = Context([Entity()], curves: new ContainmentCurves { Reason = "game constant ContainmentUtility.X is absent" });
        Assert.Equal("containment curves: game constant ContainmentUtility.X is absent",
            Assert.Throws<RequiredFactException>(() => ForecastContainment.CellCredit(Cell, Outcome(), [], curves, Day)).Fact);
        var prediction = Context([Entity()]) with { ContainmentCells = [new PlannedContainmentCell(1, 0, "entity1", "the containment defs are unread")] };
        Assert.Throws<RequiredFactException>(() => ForecastContainment.CellCredit(Cell, Outcome(), [], prediction, Day));
    }

    [Fact]
    public void TheRoundsReadTheEntitiesTheirPlatformsAndTheGamesCurves()
    {
        // The production path (#3822): the native pawn and building rows lift into the forecast; GenerateCoupledActivities
        // hands it to the harm term; the catalog reads ContainmentUtility's own curves.
        var curves = new DefinitionCatalog([new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }], DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f)).ContainmentCurves();
        static (double, double)[] Rounded(EquatableArray<CurvePoint> curve) => [.. curve.Select(p => (Math.Round(p.X, 6), Math.Round(p.Y, 6)))];
        Assert.Equal(Rounded(Curves.Escape), Rounded(curves.Escape));
        Assert.Equal(Rounded(Curves.Study), Rounded(curves.Study));
        Assert.Equal(Rounded(Curves.Captivity), Rounded(curves.Captivity));
        var forecast = RoundsContainmentReads.ContainmentForecastOf(
            [Row("entity1", held: true), Row("entity2", held: false), Row("noble", entity: false), Row("entity3", held: false, capturable: false)],
            [Platform("entity1", 5, 5, Now)], curves);
        Assert.Equal("", forecast.Unread);
        Assert.Equal([Entity(), Entity("entity2", held: false)], forecast.Entities);
        // A held entity whose platform row is missing is unread.
        Assert.StartsWith("entity1: ", RoundsContainmentReads.ContainmentForecastOf([Row("entity1", held: true)], [], curves).Unread);

        var facts = ForecastCoupledTests.ConstructionReview() with { Containment = new() { Forecast = forecast } };
        Assert.Equal(forecast, ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context.Harm.Containment);
        // A fresh colony has no entity: the forecast is empty and nothing throws.
        var fresh = ForecastCoupledCandidates.GenerateCoupledActivities(ForecastCoupledTests.ConstructionReview()
            with { Containment = new() { Forecast = RoundsContainmentReads.ContainmentForecastOf([], [], curves) } })!.Context;
        Assert.Empty(fresh.Harm.Containment.Entities);
        Assert.Empty(ForecastContainment.CellCredit(Cell, Outcome(), [], fresh, Day));
    }

    [Fact]
    public void APlannedCellIsPredictedAndHoldsTheEntityOnAPlatformInsideIt()
    {
        var defs = new ContainmentDefs { Holder = "HoldingPlatform", HolderFactor = 1, HolderBase = 25, FloorStrength = 0, WallHP = 300, DoorHP = 160 };
        var room = new PlannedRoom { Id = 4, Role = PlannedRole.ContainmentCell, Region = RegionRules.RectRegion(new Policy.Rectangle(4, 4, 3, 3)) };
        var floors = new FlooringFacts { Layout = new LayoutPlan { Rooms = [room], NextRoomId = 5 } };
        var planning = new ContainmentPlanning { Defs = new ContainmentDefsRead.Read(defs), Forecast = new() { Entities = [Entity(), Entity("entity9") with { Platform = new(new(40, 40), Now) }] } };
        Assert.Equal([new PlannedContainmentCell(4, defs.Predict(new ContainmentRoom()), "entity1")], ForecastContainment.CellsOf(planning, floors));
        Assert.Equal([new PlannedContainmentCell(4, 0, "entity1", "the Anomaly expansion is not active")],
            ForecastContainment.CellsOf(planning with { Defs = new ContainmentDefsRead.Inactive() }, floors));
        Assert.Equal([new PlannedContainmentCell(4, 0, "entity1", "Wall is not among the planning definitions")],
            ForecastContainment.CellsOf(planning with { Defs = new ContainmentDefsRead.Unread("Wall is not among the planning definitions") }, floors));
    }

    private static O.PawnState Row(string id, bool held = false, bool entity = true, bool capturable = true) => PawnFixtures.Row(id) with
    {
        Anomaly = new O.PawnAnomalyState.Active(new O.PawnAnomaly
        {
            Entity = entity, Mutant = false, MinContainmentStrength = entity ? Need : 0,
            HiddenFromPlayer = false, PsychicRitualInvoker = false, MeleeOnly = false, Creepjoiner = null,
            Held = entity ? new O.HeldState
            {
                Held = held, CanBeCaptured = capturable, Mode = O.EntityContainmentModeKind.Study, Escaping = false, ExtractBioferrite = false,
                NeedsTend = false, Bleeding = false, HarvesterAttached = false, BioferritePerDay = 0,
                Escape = new O.EntityEscapeState { BaseMtbDays = MtbDays, MovingFactor = 1, DaysInCaptivity = 0, ColdFactor = 1, CombatPower = 1000, CanEscape = held },
            } : null,
            Study = entity ? new O.StudyState { CurrentlyStudiable = true, Yield = new O.StudyYieldState { Knowledge = Knowledge, Category = "Basic", FrequencyTicks = (int)Day } } : null,
        }),
    };


    private static O.BuildingState Platform(string held, int x, int z, double strength) => new()
    {
        Building = new O.EntityRef { Id = "platform", DefName = "HoldingPlatform", Label = "holding platform", Place = new O.EntityPlace.OnMap(0, new C.Cell { X = x, Z = z }) },
        Rotation = RimGovernor.Contracts.Wire.Placement.Rotation.North,
        Stuff = null,
        Faction = null,
        Status = O.BuildingStatus.Built,
        HitPoints = new O.BuildingHitPoints.Untracked(),
        Construction = null,
        Service = BuildingServiceWire.NoService,
        Settings = new O.BuildingSettings { Forbidden = false, Control = new O.BuildingControl.None() },
        Burning = false,
        Occupied = new O.Rectangle { Minimum = new C.Cell { X = x, Z = z }, Maximum = new C.Cell { X = x, Z = z } },
        Anomaly = new O.AnomalyBuilding { Holder = new O.EntityHolderState { ContainmentStrength = strength, Available = false, HeldPawn = new C.Ref { Id = held }, Doors = [] } },
        Door = null,
    };
}
