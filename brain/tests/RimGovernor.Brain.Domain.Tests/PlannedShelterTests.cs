using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A planned shelter built from its planned sites (#3277): no placed shell blueprint, a heater standing inside.</summary>
public sealed class PlannedShelterTests
{
    internal const double Horizon = 1000;
    private static readonly Cell Inside = new(1, 1);
    private static readonly Cell WallCell = new(0, 1);
    private static readonly Cell DoorCell = new(2, 1);

    private static PlannedBuildSite Ring(OpKind op, double finish, PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible)
    {
        var kind = op == OpKind.WallIn ? "wall" : "door";
        var id = "planned/room1/" + kind + "/WoodLog";
        var cell = op == OpKind.WallIn ? WallCell : DoorCell;
        var row = new Row(id, kind == "wall" ? "Wall" : "Door", cell, "pawn", "", "", cell, 0, 0, "Finish",
            Known(0.0), default, Known(finish)) { SiteApproachTicks = Known(1.0) };
        return new PlannedBuildSite(id, 1, op, kind == "wall" ? "Wall" : "Door", "WoodLog", Rotation.North, [cell])
            { Answer = ConstructionFixtures.Answer(id, verdict) with { Options = [row] } };
    }

    private static PlannedBuildSite Roof() => new("planned/room1/roof", 1, OpKind.RoofIn, "", "", Rotation.North, [Inside]);

    private const string HeaterSite = "planned/room1/build/Heater/1/1";

    private const string CampfireSite = "planned/room1/build/Campfire/1/1";

    private static PlannedBuildSite Heater(double finish, PlannedSiteVerdict verdict = PlannedSiteVerdict.Feasible, string def = "Heater")
    {
        var id = "planned/room1/build/" + def + "/1/1";
        var row = new Row(id, def, Inside, "pawn", "", "", Inside, 0, 0, "Finish",
            Known(0.0), default, Known(finish)) { SiteApproachTicks = Known(1.0) };
        return new PlannedBuildSite(id, 1, OpKind.Build, def, "", Rotation.North, [Inside])
            { Answer = ConstructionFixtures.Answer(id, verdict) with { Options = [row] } };
    }

    private static readonly ThermalSource PlannedHeater = new(HeaterSite, Inside, false, .35, 20d, new HeatSupply.Powered("net", 175d, 500d, 60000d));

    // unplaced: nothing at all is placed, so the coupled capture holds no cell or source of the room (#3323); its cells
    // and the planned heater (plannedSources, else a powered heater) come only from the round's planned thermal read.
    private static RoundsFacts Review(IEnumerable<PlannedBuildSite> planned, double ambient = -10, bool buildRoof = false, bool unplaced = false,
        params ThermalSource[] plannedSources)
    {
        var facts = ForecastCoupledTests.ConstructionReview(stock: 100);
        var cells = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z))).ToArray();
        // Nothing is placed: the open ring cells and the unroofed interior are owed only by the planned sites.
        var open = new[] { Inside, WallCell, DoorCell };
        var thermal = CoupledThermalTests.NoThermal with
        {
            Outdoors = ambient, ThinRoofExchangePerTick = .00005, WallExchangePerTick = .000017,
            Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, ambient, "none",
                !open.Contains(c), false, "", false, false, 0, buildRoof && c == Inside))),
            Workers = [new("pawn", 5d)], RoofRoutes = [new("pawn", Inside, new RoofApproach.Path(1d))],
            Sources = [new("heater", Inside, true, .35, 20d, CoupledThermalTests.Powered(60000d))],
        };
        CoupledThermalRead? plannedThermal = null;
        if (unplaced)
        {
            plannedThermal = thermal with
            {
                Workers = [], RoofRoutes = [],
                Sources = plannedSources.Length != 0 ? EquatableArray.From(plannedSources) : [PlannedHeater],
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
            Needs = [new("food", Known(1d), Known(0d), Known(.3)), new("rest", Known(.2), Known(.0004), Known(.3))],
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
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced() };
    }

    /// <summary>The unplaced planned room with its planned heater (#3323): wall, door, roof and heater sites, the room alternative and the
    /// pawn's rest, ten roots whose schedule searches cost a few expansions each, so the cardinality sweep is affordable and the budget
    /// decides whether it runs. The third workload of the allocation measurements (<see cref="PortfolioAllocationTests"/>, #3725).</summary>
    internal static RoundsFacts UnplacedHeatedRoom() => Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50)], unplaced: true);

    private static ForecastCoupledSelection Select(RoundsFacts facts) => ForecastCoupledSelection.CompareActivityOutcomes(
        ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay, facts, 
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()), facts.CoupledAlternatives!);

    // #3724: the shelter's room-closure bundles sleep the pawn, so the urgent-zero bound is unavailable for them (a plan's sleep can leave
    // rest unassessed), the reason reported, and the pruned search returns the unpruned selection.
    [Fact]
    public void APlannedRoomsSearchReportsTheBoundUnavailableAndPrunesNothing()
    {
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof()]);
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay, facts,
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var pruned = ForecastCoupledSelection.CompareActivityOutcomes(request, facts.CoupledAlternatives!);
        var unpruned = ForecastCoupledSelection.CompareActivityOutcomesUnpruned(request, facts.CoupledAlternatives!);
        ForecastCoupledSelectionTests.AssertPrunedEquivalent(unpruned, pruned);
        Assert.Contains(ForecastCoupledSelection.UrgentZeroUnservedBound + ":rest_service_in_closure", pruned.BoundsUnavailable);
        Assert.True(pruned.SearchPhases.Sum(p => p.Unbounded) > 0);
        // A subset the bound did skip holds no sleep of the pawn's: the shelter bundles, which do, were all evaluated.
        var sleeps = facts.CoupledAlternatives!.Context.NeedTasks.Where(t => t.Kind == "rest").Select(t => t.Task).ToHashSet(StringComparer.Ordinal);
        Assert.All(pruned.Rejected.Where(r => ForecastPortfolioEvidence.BoundOf(r.Reason) is not null),
            r => Assert.All(r.Commitments, id => Assert.DoesNotContain(facts.CoupledAlternatives!.Alternatives.Single(c => c.Id == id).ActivityCompletions, sleeps.Contains)));
        Assert.Single(pruned.Selected.Shelters);
    }

    private static string Why(RoundsFacts facts, ForecastCoupledSelection result) =>
        string.Join(";", facts.CoupledAlternatives!.Findings.Concat(result.Selection.Findings.Select(f => f.ToString()))
            .Concat(facts.CoupledAlternatives.Context.Shelters.SelectMany(s => s.Findings)));

    /// <summary>
    /// #3823: on a temperate fresh colony (relief 0) the live candidates held the barracks only as its room alternative
    /// (planned/room1/WoodLog/Shell/composition/0, its site roots' composition truncated). The relief gate rejected it in
    /// every portfolio, so no sleeping ring opened. A priced room's alternative is valued by its credit and is exempt like
    /// its site roots; the same alternative of an unpriced room is still construction the gate holds.
    /// </summary>
    [Fact]
    public void APricedRoomsAlternativeAloneIsSelectableOnATemperateStartAndAnUnpricedOneIsHeldByTheReliefGate()
    {
        string[] sites = ["planned/room1/door/WoodLog", "planned/room1/wall/WoodLog"];
        RoundsFacts AlternativeOnly(RoundsFacts f) => f with { CoupledAlternatives = f.CoupledAlternatives! with
            { Alternatives = EquatableArray.From(f.CoupledAlternatives.Alternatives.Where(c => !sites.Contains(c.ExclusiveOutcome))) } };
        static bool Room(ForecastPortfolioEvidence e) => e.Commitments.Any(id => id.StartsWith("planned/room1/WoodLog/Shell/composition/", StringComparison.Ordinal));
        static string Reasons(ForecastCoupledSelection r) => string.Join(";", r.Rejected.Select(e => string.Join(",", e.Commitments) + "=" + e.Reason))
            + " selected=" + string.Join(",", r.Selection.Selected);
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30)], ambient: 20);

        var unpriced = AlternativeOnly(facts);
        Assert.False(unpriced.CoupledAlternatives!.Context.Priced(1));
        var held = Select(unpriced);
        Assert.Contains(held.Rejected, e => Room(e) && e.Reason == "construction_without_supported_service_relief:relief=0");
        Assert.DoesNotContain(held.Selection.Selected, id => id.StartsWith("planned/room1/", StringComparison.Ordinal));

        // The barracks credit (#3658): the sleeper's spot is outdoors in no room (slept_outside -10), the barracks it is booked to
        // at stage 0 gives slept_in_barracks -2.
        var outdoors = new NeedCell(Known(false), Known(false), Known(false), Known(true), Known(true));
        var sleeper = SleepFixtures.Sleeping(facts.ForecastObserved.Needs!.Pawns[0], SleepFixtures.InBed("bed", outside: true) with { Temperature = 20 }) with
        {
            ThoughtStages = [new("SleptOutside", "slept_outside", 0, -10, Known((double)GameTime.TicksPerDay), false),
                new("SleptInBarracks", "slept_in_barracks", 0, -2, Known((double)GameTime.TicksPerDay), false)],
            OwnedBed = "bed",
            Targets = [new("bed", "rest", "", 1, Known(0d), default, default, Known(.0005)) { Bed = new("", Known(0), false), Cell = outdoors, Location = Inside }],
        };
        var barracks = facts with
        {
            PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Barracks)] }),
            PlannedShelters = [facts.PlannedShelters[0] with { Role = PlannedRole.Barracks }],
            PlannedRoomPredictions = Known(EquatableArray.From([new PlannedRoomPrediction("1", "WoodLog", "Barracks", 30d, Known(0), 1, 0, false)])),
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs with { Pawns = [sleeper] } },
        };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(barracks, Horizon)!.Priced();
        barracks = AlternativeOnly(barracks with { CoupledAlternatives = generated with { Context = generated.Context with
            { BedroomOccupants = new Dictionary<int, EquatableArray<string>> { [1] = ["pawn"] }, PlannedBeds = SleepFixtures.Beds(1, 1), InvestmentHorizonDays = 10d } } });
        Assert.True(barracks.CoupledAlternatives!.Context.Priced(1));
        var result = Select(barracks);
        Assert.True(result.Selection.Selected.Any(id => id.StartsWith("planned/room1/WoodLog/Shell/composition/", StringComparison.Ordinal)), Reasons(result));
        Assert.Equal(Known(0d), result.Selected.StationaryThermalRelief);
        Assert.DoesNotContain(result.Rejected, e => Room(e) && e.Reason.StartsWith("construction_without_supported_service_relief", StringComparison.Ordinal));
    }

    /// <summary>
    /// #3828 (option A): an unpriced room's ring tied the baseline on the ledger and then won on its sites as development, so
    /// the first rounds opened unpriced rings. No planned room's unit is development now: the unpriced ring alone ties the
    /// baseline on development too and is not selected. The same ring as a barracks whose sleeper's thought it lifts
    /// (slept_outside -4 to slept_in_barracks -2) is credited on the ledger and selected.
    /// </summary>
    [Fact]
    public void AnUnpricedRingAloneDoesNotBeatTheBaselineAndAPricedBarracksWithACreditIsSelected()
    {
        static bool Room1(ForecastCoupledSelection r) => r.Selection.Selected.Any(id => id.StartsWith("planned/room1/", StringComparison.Ordinal));
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30)], ambient: 20);
        Assert.False(facts.CoupledAlternatives!.Context.Priced(1));
        var unpriced = Select(facts);
        Assert.False(Room1(unpriced), Why(facts, unpriced));
        Assert.Equal(unpriced.BaselineVector.Development, unpriced.SelectedVector.Development);
        Assert.All(unpriced.Rejected.Where(e => e.Consequences is not null && e.Commitments.Any(id => id.StartsWith("planned/room1/", StringComparison.Ordinal))),
            e => Assert.Equal(unpriced.BaselineVector.Development, e.Consequences!.Development));

        var outdoors = new NeedCell(Known(false), Known(false), Known(false), Known(true), Known(true));
        var sleeper = SleepFixtures.Sleeping(facts.ForecastObserved.Needs!.Pawns[0], SleepFixtures.InBed("bed", outside: true) with { Temperature = 20 }) with
        {
            ThoughtStages = [new("SleptOutside", "slept_outside", 0, -4, Known((double)GameTime.TicksPerDay), false),
                new("SleptInBarracks", "slept_in_barracks", 0, -2, Known((double)GameTime.TicksPerDay), false)],
            OwnedBed = "bed",
            Targets = [new("bed", "rest", "", 1, Known(0d), default, default, Known(.0005)) { Bed = new("", Known(0), false), Cell = outdoors, Location = Inside }],
        };
        var barracks = facts with
        {
            PlannedSites = Known(facts.PlannedSites.Value with { Roles = [(1, PlannedRole.Barracks)] }),
            PlannedShelters = [facts.PlannedShelters[0] with { Role = PlannedRole.Barracks }],
            PlannedRoomPredictions = Known(EquatableArray.From([new PlannedRoomPrediction("1", "WoodLog", "Barracks", 30d, Known(0), 1, 0, false)])),
            ForecastObserved = facts.ForecastObserved with { Needs = facts.ForecastObserved.Needs with { Pawns = [sleeper] } },
        };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(barracks, Horizon)!.Priced();
        barracks = barracks with { CoupledAlternatives = generated with { Context = generated.Context with
            { BedroomOccupants = new Dictionary<int, EquatableArray<string>> { [1] = ["pawn"] }, PlannedBeds = SleepFixtures.Beds(1, 1), InvestmentHorizonDays = 10d } } };
        Assert.True(barracks.CoupledAlternatives!.Context.Priced(1));
        var result = Select(barracks);
        Assert.True(Room1(result), Why(barracks, result));
        Assert.Contains(result.SelectedVector.Value.Ledger.InvestmentContributions, c => c.Key.StartsWith("barracks/1/", StringComparison.Ordinal) && c.Value < 0);
        Assert.Equal(result.BaselineVector.Development, result.SelectedVector.Development);
    }

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
        var facts = UnplacedHeatedRoom();
        var candidates = facts.CoupledAlternatives!;
        var plan = Assert.Single(candidates.Context.Shelters);
        Assert.Equal("planned/1", plan.Room.ToString());
        Assert.Empty(plan.Findings);
        Assert.Equal((HeaterSite, "net"), (plan.Sources.Single().Target, Assert.IsType<HeatSupply.Powered>(plan.Sources.Single().Supply).Network));
        // Wall, door, roof and heater: one prerequisite each, realized by the planned site's own finish.
        Assert.Equal(4, plan.AlternativePrerequisites.Count);
        var heater = candidates.Context.PlannedSites.Single(s => s.Site == HeaterSite);
        Assert.Contains(plan.AlternativePrerequisites, group => group.Any(heater.Finishes.Contains));

        // The whole-portfolio selection over every planned-site, room and rest root picks the complete shelter (#3334).
        var result = Select(facts);
        Assert.DoesNotContain("portfolio_search_truncated", result.Selection.Findings);
        Assert.True(result.Selected.StationaryThermalRelief is { IsKnown: true, Value: > 0 }, Why(facts, result)
            + " | SEL " + string.Join(",", result.Selection.Selected) + " | " + string.Join(",", result.Selected.Plan.Allocations.Select(a => a.Activity + "@" + a.Start + "-" + a.End)));
        // Its four completed sites are the planned room's, which count no development (#3828): the relief selects it.
        Assert.Equal(4, result.Selected.RequestedDevelopment.Count);
        Assert.Equal(0, result.SelectedVector.Development);
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
    /// #3673: a planned campfire is a fueled source with unlimited supply, so the shelter it heats has a known projection, and
    /// the selected outcome is charged its burn from the shell's completion to the horizon at the fuel's material value.
    /// </summary>
    [Fact]
    public void APlannedCampfireHeatsItsShelterAndIsChargedItsFuelFromTheShellsCompletion()
    {
        const double burn = 10d / GameTime.TicksPerDay, perLog = .01;
        var campfire = new ThermalSource(CampfireSite, Inside, false, 21 / 60d, 23d, new HeatSupply.Burning("WoodLog", burn));
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50, def: "Campfire")], unplaced: true, plannedSources: [campfire]);
        var plan = Assert.Single(facts.CoupledAlternatives!.Context.Shelters);
        Assert.Empty(plan.Findings);
        var source = plan.Sources.Single();
        Assert.Equal(new HeatSupply.Burning("WoodLog", burn), source.Supply);
        Assert.Equal([new Resource("WoodLog")], ForecastValue.ChargedMaterials(facts.CoupledAlternatives.Context));
        var alternatives = facts.CoupledAlternatives with { Context = facts.CoupledAlternatives.Context with
            { MaterialValues = new Dictionary<Resource, ConstructionMaterialValue> { [new("WoodLog")] = new(MaterialValueBasis.Labor, perLog) } } };
        facts = facts with { CoupledAlternatives = alternatives };

        var result = Select(facts);
        var shelter = Assert.Single(result.Selected.Shelters);
        Assert.True(shelter.TemperatureAtHorizon.IsKnown, Why(facts, result));
        Assert.True(result.Selected.StationaryThermalRelief is { IsKnown: true, Value: > 0 }, Why(facts, result));
        var row = Assert.Single(ForecastValue.Of(result.Selected, alternatives.Context, Horizon).InvestmentContributions, r => r.Key == "material/WoodLog");
        var units = burn * (Horizon - shelter.CompleteAt.Value);
        Assert.Equal((CampfireSite, ""), (row.Activity, row.Pawn));
        Assert.Equal(units, row.Units, 12);
        Assert.Equal(units * perLog, row.Value, 12);
    }

    /// <summary>
    /// #3679: a room heated by a planned heater and two planned fueled sources needs each planned source's own finish, and a
    /// selected outcome charges each fueled source its own fuel.
    /// </summary>
    [Fact]
    public void EachPlannedSourceGatesTheShelterOnItsOwnFinishAndEachFueledOneIsChargedItsOwnFuel()
    {
        const double burn = 10d / GameTime.TicksPerDay, oil = 2d / GameTime.TicksPerDay;
        const string lampSite = "planned/room1/build/Lamp/1/1";
        var campfire = new ThermalSource(CampfireSite, Inside, false, .1, 23d, new HeatSupply.Burning("WoodLog", burn));
        var lamp = new ThermalSource(lampSite, Inside, false, .05, 15d, new HeatSupply.Burning("Chemfuel", oil));
        var facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50), Heater(20, def: "Campfire"), Heater(10, def: "Lamp")],
            unplaced: true, plannedSources: [PlannedHeater, campfire, lamp]);
        var candidates = facts.CoupledAlternatives!;
        var plan = Assert.Single(candidates.Context.Shelters);
        Assert.Empty(plan.Findings);
        Assert.Equal([HeaterSite, CampfireSite, lampSite], plan.Sources.Select(s => s.Target));
        // Wall, door, roof and one prerequisite per planned source, each realized by that source's own finish.
        Assert.Equal(6, plan.AlternativePrerequisites.Count);
        foreach (var site in new[] { HeaterSite, CampfireSite, lampSite })
        {
            var finishes = candidates.Context.PlannedSites.Single(s => s.Site == site).Finishes;
            Assert.Single(plan.AlternativePrerequisites, group => group.Any(finishes.Contains) && group.All(finishes.Contains));
        }
        Assert.Equal([new Resource("Chemfuel"), new Resource("WoodLog")], ForecastValue.ChargedMaterials(candidates.Context));

        // The charge, over a schedule the bounded search completes: the planned campfire beside a standing lamp.
        facts = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(20, def: "Campfire")], unplaced: true,
            plannedSources: [campfire, lamp with { Complete = true }]);
        candidates = facts.CoupledAlternatives!;
        var alternatives = candidates with { Context = candidates.Context with { MaterialValues = new Dictionary<Resource, ConstructionMaterialValue>
            { [new("WoodLog")] = new(MaterialValueBasis.Labor, .01), [new("Chemfuel")] = new(MaterialValueBasis.Labor, .03) } } };
        facts = facts with { CoupledAlternatives = alternatives };

        var result = Select(facts);
        var shelter = Assert.Single(result.Selected.Shelters);
        Assert.True(shelter.TemperatureAtHorizon.IsKnown, Why(facts, result));
        var rows = ForecastValue.Of(result.Selected, alternatives.Context, Horizon).InvestmentContributions.Where(r => r.Key.StartsWith("material/", StringComparison.Ordinal)
            && r.Pawn.Length == 0).OrderBy(r => r.Activity, StringComparer.Ordinal).ToArray();
        var remaining = Horizon - shelter.CompleteAt.Value;
        Assert.Equal([(CampfireSite, "material/WoodLog"), (lampSite, "material/Chemfuel")], rows.Select(r => (r.Activity, r.Key)));
        Assert.Equal(burn * remaining, rows[0].Units, 12);
        Assert.Equal(burn * remaining * .01, rows[0].Value, 12);
        Assert.Equal(oil * remaining, rows[1].Units, 12);
        Assert.Equal(oil * remaining * .03, rows[1].Value, 12);
    }

    /// <summary>
    /// #3679: a built source needs no work, while a planned one beside it still gates the shelter on its own site: an unvalued
    /// planned heater leaves the room unvalued though a built torch stands in it.
    /// </summary>
    [Fact]
    public void ABuiltSourceDoesNotStandInForAnUnvaluedPlannedOne()
    {
        var torch = new ThermalSource("torch", Inside, true, .05, 23d, new HeatSupply.Burning("WoodLog", .0001));
        var built = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50)], unplaced: true, plannedSources: [PlannedHeater, torch]);
        var plan = Assert.Single(built.CoupledAlternatives!.Context.Shelters);
        Assert.Empty(plan.Findings);
        // Wall, door, roof and the planned heater; the built torch adds nothing.
        Assert.Equal(4, plan.AlternativePrerequisites.Count);

        var unvalued = Review([Ring(OpKind.WallIn, 40), Ring(OpKind.DoorIn, 30), Roof(), Heater(50, PlannedSiteVerdict.Unestimable)], unplaced: true,
            plannedSources: [PlannedHeater, torch]);
        Assert.Equal(["planned_heat_source_unvalued"], Assert.Single(unvalued.CoupledAlternatives!.Context.Shelters).Findings);
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
            PlannedRules.PlannedSelectionOf(candidates, result with { Selection = result.Selection with { Selected = roots } }, facts.PlannedSites)!;

        var record = Record(shelter);
        Assert.Equal([OpKind.Build, OpKind.DoorIn, OpKind.RoofIn, OpKind.WallIn], record.Sites.Select(s => s.Op));
        Assert.Equal(record, Record([.. sites.Where(id => candidates.Alternatives.Any(c => c.Id == id))]));
        var (gate, selected) = PlannedRules.PlannedRoomGateOf(record, new PlannedRoom { Id = 1 }, started: false);
        Assert.Equal(PlannedRoomGate.Selected, gate);
        var heater = new WantedPiece { DefName = "Heater", Minimum = Inside, Maximum = Inside, Size = new(1, 1), Rot = Rotation.North };
        IReadOnlyList<Operation> ops =
        [
            new() { Kind = OpKind.WallIn, Cells = [WallCell] },
            new() { Kind = OpKind.DoorIn, Cells = [DoorCell] },
            new() { Kind = OpKind.RoofIn, Cells = [Inside] },
            new() { Kind = OpKind.Build, Pieces = [heater] },
            new() { Kind = OpKind.FloorIn, Floors = [new() { Cell = Inside, DefName = "WoodPlankFloor", Designated = false }] },
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
            new PlannedBuildSite("planned/room1/build/Bed/1/1", 1, OpKind.Build, "Bed", "WoodLog", Rotation.North, [Inside]),
            new PlannedBuildSite("planned/room1/build/Campfire/1/1", 1, OpKind.Build, "Campfire", "", Rotation.North, [Inside]) };
        PlannedRoom[] rooms = [
            new() { Id = 1, Region = RegionRules.RectRegion(new(1, 1, 1, 1)), Door = DoorCell },
            new() { Id = 2, Region = RegionRules.RectRegion(new(10, 10, 11, 3)) }, // wider than the shelter roof bound
            new() { Id = 3, Region = RegionRules.RectRegion(new(20, 20, 2, 2)) },  // nothing owed: already built
            new() { Id = 4, Region = RegionRules.RectRegion(new(30, 30, 2, 2)), Outdoor = true }];
        var request = PlannedRules.PlannedThermalRequestOf(rooms, sites, ["Heater"])!;
        Assert.Equal([new PlannedThermalRoom(Inside, 1, 1)], request.Rooms);
        Assert.Equal([new PlannedThermalHeater(HeaterSite, "Heater", Inside)], request.Heaters);
        // A fueled heater (#3673) is asked for beside the electric one.
        Assert.Equal([new PlannedThermalHeater("planned/room1/build/Campfire/1/1", "Campfire", Inside), new PlannedThermalHeater(HeaterSite, "Heater", Inside)],
            PlannedRules.PlannedThermalRequestOf(rooms, sites, ["Heater", "Campfire"])!.Heaters);
        Assert.Empty(PlannedRules.PlannedThermalRequestOf(rooms, sites, [""])!.Heaters);
        Assert.Null(PlannedRules.PlannedThermalRequestOf(rooms.Skip(1), sites, ["Heater"]));
    }

    [Fact]
    public void TheCoupledCaptureKeepsItsCellsAndSourcesOverThePlannedRead()
    {
        ThermalCell Row(Cell cell, double temperature) => new(cell, temperature, "none", false, false, "", false, false, 0, false);
        var observed = CoupledThermalTests.NoThermal with { Cells = [Row(Inside, 5)], Sources = [new("heater", Inside, true, .35, 20d, CoupledThermalTests.Powered(60000d))] };
        var planned = CoupledThermalTests.NoThermal with
        {
            Cells = [Row(Inside, -10), Row(WallCell, -10)],
            Sources = [new("heater", Inside, false, .35, 20d, new HeatSupply.Unconnected()), new(HeaterSite, Inside, false, .35, 20d, new HeatSupply.Unconnected())],
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
