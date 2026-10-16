using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

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

    private static RoundsFacts Review(IEnumerable<PlannedBuildSite> planned, double ambient = -10, bool buildRoof = false)
    {
        var facts = ForecastCoupledTests.ConstructionReview(stock: 100);
        var cells = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z))).ToArray();
        // Nothing is placed: the open ring cells and the unroofed interior are owed only by the planned sites.
        var open = new[] { Inside, WallCell, DoorCell };
        var thermal = new CoupledThermalRead
        {
            Outdoors = Known(ambient), ThinRoofExchangePerTick = Known(.00005), WallExchangePerTick = Known(.000017),
            Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(ambient), "none",
                !open.Contains(c), false, false, "", "", false, false, 0, buildRoof && c == Inside))),
            Workers = [new("pawn", Known(5d))], RoofRoutes = [new("pawn", Inside, Known(1d))],
            Sources = [new("heater", Inside, true, Known(.35), Known(20d), Known(60000d))],
        };
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Awake = Known(true), LocalHour = Known(6d), SleepThreshold = Known(.75), WakeThreshold = Known(1d), SleepBlockedUntilTicks = Known(200d),
            ComfortableMin = Known(10d), ComfortableMax = Known(30d),
            MoodUnclampedTarget = Known(.5), MoodIntervalTicks = Known(150d), MoodRisePerTick = Known(.12 / GameTime.TicksPerHour),
            MoodFallPerTick = Known(.08 / GameTime.TicksPerHour), MoodFreezesWhileAsleep = Known(true), BreakThreshold = Known(.3),
            ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]),
            Needs = [new("food", Known(1d), Known(0d), Known(.3), Known(1d)), new("rest", Known(.2), Known(0d), Known(.3), Known(1d))],
            Targets = [new("bed", "rest", "", 1, Known(0d), default, default, Known(.0005)) { Location = Inside }],
        };
        facts = facts with
        {
            PlannedShelters = [new() { Id = 1, Region = RectRegion(new(1, 1, 1, 1)), Door = DoorCell }],
            PlannedSites = Known(new PlannedSiteFacts { Sites = EquatableArray.From(planned) }),
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
        facts = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = GenerateCoupledActivities(facts, Horizon) };
    }

    private static ForecastCoupledSelection Select(RoundsFacts facts) => CompareActivityOutcomes(
        CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay, facts, new(),
            new() { Work = ForecastWorkInputsOf(facts) }, [], new()), facts.CoupledAlternatives!);

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
    }
}
