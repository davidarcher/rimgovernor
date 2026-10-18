using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A planned shelter built from its planned sites (#3277): no placed shell blueprint, a heater standing inside.</summary>
public sealed class PlannedShelterTests
{
    private const double Horizon = 1000;
    private static readonly Cell Inside = new(1, 1);
    private static readonly Cell WallCell = new(0, 1);
    private static readonly Cell DoorCell = new(2, 1);

    private static PlannedBuildSite Ring(OpKind op, double finish, PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible)
    {
        var kind = op == OpKind.WallIn ? "wall" : "door";
        var id = "planned/room1/" + kind + "/WoodLog";
        var cell = op == OpKind.WallIn ? WallCell : DoorCell;
        var row = new ConstructionActivityOption(id, kind == "wall" ? "Wall" : "Door", cell, "pawn", "", "", cell, 0, 0, "Finish",
            Known(0.0), default, Known(finish)) { SiteApproachTicks = Known(1.0) };
        return new PlannedBuildSite(id, 1, op, kind == "wall" ? "Wall" : "Door", "WoodLog", Rotation.North, [cell])
            { Verdict = verdict, CanPlace = Known(true), Options = [row] };
    }

    private static PlannedBuildSite Roof() => new("planned/room1/roof", 1, OpKind.RoofIn, "", "", Rotation.North, [Inside]);

    private const string HeaterSite = "planned/room1/build/Heater/1/1";

    private static PlannedBuildSite Heater(double finish, PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible)
    {
        var row = new ConstructionActivityOption(HeaterSite, "Heater", Inside, "pawn", "", "", Inside, 0, 0, "Finish",
            Known(0.0), default, Known(finish)) { SiteApproachTicks = Known(1.0) };
        return new PlannedBuildSite(HeaterSite, 1, OpKind.Build, "Heater", "", Rotation.North, [Inside])
            { Verdict = verdict, CanPlace = Known(true), Options = [row] };
    }

    // unplaced: nothing at all is placed, so the coupled capture holds no cell or source of the room (#3323); its cells
    // and the planned heater come only from the round's planned thermal read.
    private static RoundsFacts Review(IEnumerable<PlannedBuildSite> planned, double ambient = -10, bool buildRoof = false, bool unplaced = false)
    {
        var facts = ForecastCoupledTests.ConstructionReview(stock: 100);
        var cells = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z))).ToArray();
        // Nothing is placed: the open ring cells and the unroofed interior are owed only by the planned sites.
        var open = new[] { Inside, WallCell, DoorCell };
        var thermal = new CoupledThermalRead
        {
            Outdoors = Known(ambient), ThinRoofExchangePerTick = Known(.00005), WallExchangePerTick = Known(.000017),
            Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(ambient), "none",
                !open.Contains(c), false, false, "", false, false, 0, buildRoof && c == Inside))),
            Workers = [new("pawn", Known(5d))], RoofRoutes = [new("pawn", Inside, Known(1d))],
            Sources = [new("heater", Inside, true, Known(.35), Known(20d), Known(60000d))],
        };
        CoupledThermalRead? plannedThermal = null;
        if (unplaced)
        {
            plannedThermal = thermal with
            {
                Workers = [], RoofRoutes = [],
                Sources = [new(HeaterSite, Inside, false, Known(.35), Known(20d), Known(60000d))
                    { PowerNetwork = "net", AdditionalWatts = Known(175d), SurplusWatts = Known(500d) }],
            };
            thermal = thermal with { Cells = [], Sources = [], RoofRoutes = [] };
        }
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Awake = Known(true), LocalHour = Known(6d), SleepThreshold = Known(.75), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(200d),
            ComfortableMin = Known(10d), ComfortableMax = Known(30d),
            MoodUnclampedTarget = Known(.5), MoodIntervalTicks = Known(150d), MoodRisePerTick = Known(.12 / GameTime.TicksPerHour),
            MoodFallPerTick = Known(.08 / GameTime.TicksPerHour), MoodFreezesWhileAsleep = Known(true), BreakThreshold = Known(.3),
            ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]),
            Needs = [new("food", Known(1d), Known(0d), Known(.3)), new("rest", Known(.2), Known(0d), Known(.3))],
            Targets = [new("bed", "rest", "", 1, Known(0d), default, default, Known(.0005)) { Location = Inside }],
        };
        facts = facts with
        {
            PlannedShelters = [new() { Id = 1, Region = RegionRules.RectRegion(new(1, 1, 1, 1)), Door = DoorCell }],
            // The planned-site read's routes between the sites (#3313): the roof is worked at its own cell.
            PlannedSites = Known(new PlannedSiteFacts { Sites = EquatableArray.From(planned), Routes = EquatableArray.From(cells
                .SelectMany(a => cells.Where(b => a != b).Select(b => new CoupledRoute("pawn", a, b, Known(1d))))
                .Concat(cells.Select(b => new CoupledRoute("pawn", b, b, Known(1d)) { FromStart = true }))) }),
            PlannedThermal = plannedThermal,
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

    private static ForecastCoupledSelection Select(RoundsFacts facts) => ForecastCoupledSelection.CompareActivityOutcomes(
        ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay, facts, 
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()), facts.CoupledAlternatives!);

    private static string Why(RoundsFacts facts, ForecastCoupledSelection result) =>
        string.Join(";", facts.CoupledAlternatives!.Findings.Concat(result.Selection.Findings.Select(f => f.ToString()))
            .Concat(facts.CoupledAlternatives.Context.Shelters.SelectMany(s => s.Findings)));

    [Fact]
    public void APlannedRoomWithNoPlacedShellIsAValuedShelterBuiltFromItsPlannedSites()
    {
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof()]);
        var candidates = facts.CoupledAlternatives!;
        var plan = Assert.Single(candidates.Context.Shelters);
        Assert.Equal("planned/1", plan.Room.ToString());
        Assert.DoesNotContain("unmodeled_shell_work", plan.Findings);
        Assert.Empty(plan.Findings);
        // One prerequisite per planned kind, each realized by the planned site's own finish: shared, never repeated.
        Assert.Equal(3, plan.AlternativePrerequisites.Count);
        Assert.All(plan.AlternativePrerequisites, group => Assert.NotEmpty(group));
        Assert.DoesNotContain(candidates.Context.Activities.Activities, a => a.Id.StartsWith("roof/", StringComparison.Ordinal));

        var result = Select(facts);
        Assert.True(result.Selected.StationaryThermalRelief.Value > 0, Why(facts, result));
        var shelter = Assert.Single(result.Selected.Shelters);
        Assert.True(shelter.CompleteAt.Value < Horizon, Why(facts, result));
        // The shell and roof are builder work in the same schedule as the pawn's rest.
        var finishes = candidates.Context.PlannedSites.Where(s => s.Site is "planned/room1/wall/WoodLog" or "planned/room1/door/WoodLog" or "planned/room1/roof")
            .SelectMany(s => s.Finishes).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, candidates.Context.PlannedSites.Count(s => s.Finishes.Any(finishes.Contains)));
        Assert.Equal(3, result.Selected.Plan.Allocations.Count(a => finishes.Contains(a.Activity)));
        Assert.Contains(result.Selected.Plan.Allocations, a => candidates.Context.NeedTasks.Any(n => n.Task == a.Activity && n.Kind == "rest"));
    }

    [Fact]
    public void AnUnplacedPlannedRoomWithAPlannedHeaterIsAValuedShelterBuiltByOneBuilder()
    {
        // #3323: no blueprint, frame or heater stands; the planned thermal read gives the cells and the planned heater.
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50)], unplaced: true);
        var candidates = facts.CoupledAlternatives!;
        var plan = Assert.Single(candidates.Context.Shelters);
        Assert.Equal("planned/1", plan.Room.ToString());
        Assert.Empty(plan.Findings);
        Assert.Equal(HeaterSite, plan.Source);
        Assert.Equal("net", plan.PowerNetwork);
        // Wall, door, roof and heater: one prerequisite each, realized by the planned site's own finish.
        Assert.Equal(4, plan.AlternativePrerequisites.Count);
        var heater = candidates.Context.PlannedSites.Single(s => s.Site == HeaterSite);
        Assert.Contains(plan.AlternativePrerequisites, group => group.Any(heater.Finishes.Contains));

        // The whole-portfolio selection over every planned-site, room and rest root picks the complete shelter (#3334).
        var result = Select(facts);
        Assert.DoesNotContain("portfolio_search_truncated", result.Selection.Findings);
        Assert.True(result.Selected.StationaryThermalRelief is { IsKnown: true, Value: > 0 }, Why(facts, result)
            + " | SEL " + string.Join(",", result.Selection.Selected) + " | " + string.Join(",", result.Selected.Plan.Allocations.Select(a => a.Activity + "@" + a.Start + "-" + a.End)));
        // Each completed site is one unit of requested development, whichever root built it.
        Assert.Equal(4, result.SelectedVector.Development);
        var shelter = Assert.Single(result.Selected.Shelters);
        Assert.True(shelter.CompleteAt.Value < Horizon, Why(facts, result));
        // Shell, roof and heater are the one builder's work in the same schedule as the pawn's rest.
        var finishes = candidates.Context.PlannedSites.Where(s => s.Site is "planned/room1/wall/WoodLog" or "planned/room1/door/WoodLog"
            or "planned/room1/roof" or HeaterSite).SelectMany(s => s.Finishes).ToHashSet(StringComparer.Ordinal);
        var built = result.Selected.Plan.Allocations.Where(a => finishes.Contains(a.Activity)).ToArray();
        Assert.Equal(4, built.Length);
        Assert.All(built, a => Assert.Equal("pawn", a.Pawn));
        // One builder's capacity: the four finishes run one after another, never overlapping.
        var ordered = built.OrderBy(a => a.Start).ToArray();
        Assert.All(ordered.Zip(ordered.Skip(1)), pair => Assert.True(pair.Second.Start >= pair.First.End, Why(facts, result)));
        Assert.Contains(result.Selected.Plan.Allocations, a => candidates.Context.NeedTasks.Any(n => n.Task == a.Activity && n.Kind == "rest"));
    }

    /// <summary>
    /// The handoff follows what the selected plan completes, not its root ids (#3342): the shelter root alone, or the
    /// site roots its schedule equals, record the room's wall, door, roof and heater, and the reconcile places all four.
    /// </summary>
    [Fact]
    public void ASelectedShelterRootPlacesItsRoomsShellRoofAndHeaterSites()
    {
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50)], unplaced: true);
        var candidates = facts.CoupledAlternatives!;
        var result = Select(facts);
        var shelter = Assert.Single(candidates.Alternatives, c => c.Id.StartsWith("shelter/planned/1", StringComparison.Ordinal)).Id;
        string[] sites = ["planned/room1/build/Heater/1/1", "planned/room1/door/WoodLog", "planned/room1/roof", "planned/room1/wall/WoodLog"];
        PlannedSelection Record(params string[] roots) =>
            PlannedRules.PlannedSelectionOf(candidates, result with { Selection = result.Selection with { Selected = roots } }, facts.PlannedSites, 100)!;

        var record = Record(shelter);
        Assert.Equal(sites, record.Sites.Select(s => s.Id));
        Assert.Equal(record, Record([.. sites.Where(id => candidates.Alternatives.Any(c => c.Id == id))]));
        var (gate, selected) = PlannedRules.PlannedRoomGateOf(record, 1, started: false);
        Assert.Equal(PlannedRoomGate.Selected, gate);
        var heater = new WantedPiece { DefName = "Heater", Minimum = Inside, Maximum = Inside, Size = new(1, 1), Rot = Rotation.North };
        IReadOnlyList<Operation> ops =
        [
            new() { Kind = OpKind.WallIn, Cells = [WallCell] },
            new() { Kind = OpKind.DoorIn, Cells = [DoorCell] },
            new() { Kind = OpKind.RoofIn, Cells = [Inside] },
            new() { Kind = OpKind.Build, Pieces = [heater] },
            new() { Kind = OpKind.FloorIn, Floors = [new() { Cell = Inside, DefName = "WoodPlankFloor" }] },
        ];
        Assert.Equal([OpKind.WallIn, OpKind.DoorIn, OpKind.RoofIn, OpKind.Build], PlannedRules.SelectedPlannedOps(ops, selected).Select(o => o.Kind));
    }

    [Fact]
    public void WithoutThePlannedThermalReadAnUnplacedRoomStaysExplicitlyUnknown()
    {
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50)], unplaced: true) with { PlannedThermal = null };
        var plan = Assert.Single(ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Context.Shelters);
        Assert.Contains("missing_shelter_geometry", plan.Findings);
        Assert.Contains("unknown_heat_source", plan.Findings);
    }

    [Fact]
    public void AnUnvaluedPlannedHeaterLeavesTheShelterUnvalued()
    {
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50, PlannedSiteVerdict.Unestimable)], unplaced: true);
        var plan = Assert.Single(facts.CoupledAlternatives!.Context.Shelters);
        Assert.Equal(["planned_heat_source_unvalued"], plan.Findings);
        Assert.False(Select(facts).Selected.StationaryThermalRelief is { IsKnown: true, Value: > 0 });
    }

    [Fact]
    public void ThePlannedThermalRequestAsksForUnbuiltRectangularShelterRoomsAndTheirHeaters()
    {
        var sites = new[] { Ring(OpKind.WallIn, 40), Heater(50),
            new PlannedBuildSite("planned/room2/wall/WoodLog", 2, OpKind.WallIn, "Wall", "WoodLog", Rotation.North, [new(10, 9)]),
            new PlannedBuildSite("planned/room1/build/Bed/1/1", 1, OpKind.Build, "Bed", "WoodLog", Rotation.North, [Inside]) };
        PlannedRoom[] rooms = [
            new() { Id = 1, Region = RegionRules.RectRegion(new(1, 1, 1, 1)), Door = DoorCell },
            new() { Id = 2, Region = RegionRules.RectRegion(new(10, 10, 11, 3)) }, // wider than the shelter roof bound
            new() { Id = 3, Region = RegionRules.RectRegion(new(20, 20, 2, 2)) },  // nothing owed: already built
            new() { Id = 4, Region = RegionRules.RectRegion(new(30, 30, 2, 2)), Outdoor = true }];
        var request = PlannedRules.PlannedThermalRequestOf(rooms, sites, "Heater")!;
        Assert.Equal([new PlannedThermalRoom(Inside, 1, 1)], request.Rooms);
        Assert.Equal([new PlannedThermalHeater(HeaterSite, "Heater", Inside)], request.Heaters);
        Assert.Empty(PlannedRules.PlannedThermalRequestOf(rooms, sites, "")!.Heaters);
        Assert.Null(PlannedRules.PlannedThermalRequestOf(rooms.Skip(1), sites, "Heater"));
    }

    [Fact]
    public void TheCoupledCaptureKeepsItsCellsAndSourcesOverThePlannedRead()
    {
        ThermalCell Row(Cell cell, double temperature) => new(cell, Known(temperature), "none", false, false, false, "", false, false, 0, false);
        var observed = new CoupledThermalRead { Cells = [Row(Inside, 5)], Sources = [new("heater", Inside, true, Known(.35), Known(20d), Known(60000d))] };
        var planned = new CoupledThermalRead
        {
            Cells = [Row(Inside, -10), Row(WallCell, -10)],
            Sources = [new("heater", Inside, false, default, default, default), new(HeaterSite, Inside, false, Known(.35), Known(20d), default)],
        };
        var joined = PlannedRules.WithPlannedThermal(observed, planned)!;
        Assert.Equal([Row(Inside, 5), Row(WallCell, -10)], joined.Cells);
        Assert.Equal(["heater", HeaterSite], joined.Sources.Select(s => s.Target));
        Assert.True(joined.Sources[0].Complete);
        Assert.Same(observed, PlannedRules.WithPlannedThermal(observed, null));
        Assert.Same(planned, PlannedRules.WithPlannedThermal(null, planned));
    }

    [Fact]
    public void AnUnvaluedPlannedWallLeavesTheShelterUnvaluedNotUnmodeled()
    {
        var facts = Review([Ring(OpKind.WallIn, 40, PlannedSiteVerdict.Unestimable), Ring(OpKind.DoorIn, 30), Roof()]);
        var plan = Assert.Single(facts.CoupledAlternatives!.Context.Shelters);
        Assert.Contains("planned_shell_unvalued", plan.Findings);
        Assert.DoesNotContain("unmodeled_shell_work", plan.Findings);
        Assert.False(Select(facts).Selected.StationaryThermalRelief is { IsKnown: true, Value: > 0 });
    }

    [Fact]
    public void AnOpenRingCellNoPlannedSiteCoversStaysUnmodeled()
    {
        var plan = Assert.Single(Review([Ring(OpKind.DoorIn, 30), Roof()]).CoupledAlternatives!.Context.Shelters);
        Assert.Contains("unmodeled_shell_work", plan.Findings);
    }

    [Fact]
    public void WithoutAGeneratedPlannedRoofTheDesignatedCellsKeepTheirNativeRoofWork()
    {
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30)], buildRoof: true);
        var candidates = facts.CoupledAlternatives!;
        var plan = Assert.Single(candidates.Context.Shelters);
        Assert.DoesNotContain("unmodeled_shell_work", plan.Findings);
        var roof = Assert.Single(candidates.Context.Activities.Activities, a => a.Id.StartsWith("roof/planned/1/1/1", StringComparison.Ordinal));
        // The native roof cell waits on the planned shell it stands on.
        foreach (var site in new[] { "planned/room1/wall/WoodLog", "planned/room1/door/WoodLog" })
            Assert.Contains(candidates.Context.PlannedSites.Single(s => s.Site == site).Finishes, roof.Prerequisites.Contains);

        // The whole-portfolio selection picks the complete shelter over the shell alone (#3334): the per-cell roof is the
        // room's one roof unit, so the shelter's wall, door and roof outrank the shell's two sites.
        var result = Select(facts);
        Assert.DoesNotContain("portfolio_search_truncated", result.Selection.Findings);
        Assert.True(result.Selected.StationaryThermalRelief is { IsKnown: true, Value: > 0 }, Why(facts, result)
            + " | SEL " + string.Join(",", result.Selection.Selected));
        Assert.Equal(["planned/room1/door/WoodLog", "planned/room1/roof", "planned/room1/wall/WoodLog"], result.Selected.RequestedDevelopment);
        Assert.True(Assert.Single(result.Selected.Shelters).CompleteAt.Value < Horizon, Why(facts, result));
        Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity == roof.Id);
    }
}
