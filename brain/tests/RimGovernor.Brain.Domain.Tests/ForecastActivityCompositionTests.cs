using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastActivityCompositionTests
{
    [Fact]
    public void ExpensiveEarlierRootsCannotStarveAnIndependentLaterRoot()
    {
        var facts = ForecastCoupledSelectionTests.Review();
        // Eligibility alone is not physical inventory. Earlier recipe roots have
        // many quantity combinations but their observed ingredient stock is empty.
        var conversions = Enumerable.Range(0, 12).Select(i => new CoupledFoodConversion("empty-source", "pawn", "bench-" + i,
            "recipe-" + i, "Rice", 10, "Meal", 1, new(1, 1), new(2, 1), new(3, 1),
            Known(0d), Known(1d), Known(10d), Known(1d), 10, 10)
            { RequiresHarvest = false, Bill = "bill-" + i, WorkGiver = "Cook" }).ToArray();
        var read = facts.ForecastObserved.Coupled! with
        {
            Food = facts.ForecastObserved.Coupled!.Food with { Options = [] },
            FoodSources = [new("empty-source", "Rice", new(1, 1), 0, Known(1000d))],
            FoodConversions = EquatableArray.From(conversions),
            FoodUses = [new("pawn", "Meal", new(3, 1), Known(true), Known(.8), Known(10d), 1, Known(1000d))],
            Support = new() { Known = true,
                Demands = [new("patient-care", "care", "patient", new(1, 1), default, Known(0d))],
                Workers = [new("patient-care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), Known(0d), Known(10d))] }
        };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = read } };
        var result = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300));
        var reordered = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts with
        { ForecastObserved = facts.ForecastObserved with { Coupled = read with { FoodConversions = EquatableArray.From(conversions.Reverse()) } } }, 300));
        Assert.True(result.Truncated);
        Assert.InRange(result.CompositionExpansions, 1, 16384);
        Assert.Contains(result.Context.Activities.Activities, t => t.Id.StartsWith("support/care/", StringComparison.Ordinal));
        Assert.Equal(result.CompositionExpansions, reordered.CompositionExpansions);
        Assert.Equal(result.Findings, reordered.Findings);
        Assert.Equal(result.Context.Activities.Activities.Select(t => t.Id), reordered.Context.Activities.Activities.Select(t => t.Id));
    }
    private static readonly string[] CookingPrerequisites = ["build", "carry-a", "carry-b"];
    private static readonly string[] Consumers = ["first", "second"];
    private static readonly string[] IngredientLots = ["a", "b"];
    private static ForecastCompositionSupply Supply(string id, string resource, int count, string location = "bench", string producer = "",
        ForecastRequirementKind kind = ForecastRequirementKind.Consumed) => new(id, new(resource), Known(count), location, Known(0d), Known(100d))
        { Producer = producer, Lot = id, Provenance = "observed/" + id, Kind = kind, Capacity = kind == ForecastRequirementKind.Exclusive ? id : "" };
    private static ForecastCompositionRequirement Need(string id, string resource, int count, string[] sources, string location = "bench",
        ForecastRequirementKind kind = ForecastRequirementKind.Consumed) => new(id, new(resource), Known(count), location, kind,
            Known(EquatableArray.From(sources)), Known(0d), Known(100d));
    private static ForecastCompositionOperation Op(string id, ForecastCompositionRequirement[]? requirements = null,
        ForecastCompositionSupply[]? supplies = null, string location = "bench", string end = "bench") =>
        new(new(id, location, end, [])
        {
            Outputs = EquatableArray.From((supplies ?? []).Where(s => s.Kind == ForecastRequirementKind.Consumed)
                .Select(s => new ForecastActivityYield(s.Resource, Known((double)s.Units.Value)) { Lot = s.Lot, LotLocation = s.Location }))
        }, EquatableArray.From(requirements ?? []), EquatableArray.From(supplies ?? []));
    private static ForecastCompositionRequest Request(ForecastCompositionOperation[] operations, ForecastCompositionSupply[] observed,
        params string[] roots) => new() { Operations = EquatableArray.From(operations), Observed = EquatableArray.From(observed), Roots = EquatableArray.From(roots), Horizon = 100 };

    [Fact]
    public void SharedClosureConnectsPartialSourcesDeliveryCookingUseAndFacilityConstruction()
    {
        var riceA = Supply("rice-a", "Rice", 3, "field", "harvest");
        var riceB = Supply("rice-b", "Rice", 2, "field");
        var atBenchA = Supply("delivered-a", "Rice", 3, producer: "carry-a") with { Lot = riceA.Lot };
        var atBenchB = Supply("delivered-b", "Rice", 2, producer: "carry-b") with { Lot = riceB.Lot };
        var meal = Supply("meal", "Meal", 1, producer: "cook");
        var facility = Supply("built-stove", "StoveCapability", 1, producer: "build", kind: ForecastRequirementKind.Reusable);
        var existing = Supply("existing-stove", "StoveCapability", 1, kind: ForecastRequirementKind.Reusable);
        var request = Request([
            Op("harvest", supplies: [riceA], location: "field", end: "field"),
            Op("carry-a", [Need("load-a", "Rice", 3, [riceA.Id], "field")], [atBenchA], "field"),
            Op("carry-b", [Need("load-b", "Rice", 2, [riceB.Id], "field")], [atBenchB], "field"),
            Op("build", [Need("wood", "Wood", 2, ["wood"])], [facility]),
            Op("cook", [Need("ingredients", "Rice", 5, [atBenchA.Id, atBenchB.Id]),
                Need("stove", "StoveCapability", 1, [facility.Id, existing.Id], kind: ForecastRequirementKind.Reusable)], [meal]),
            Op("eat", [Need("serving", "Meal", 1, [meal.Id])])
        ], [riceB, Supply("wood", "Wood", 2), existing], "eat");
        var result = ForecastActivityComposition.ComposeActivityDependencies(request);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.Graphs.Count);
        var built = result.Graphs.Single(g => g.Activities.Any(a => a.Id == "build"));
        Assert.Equal(1, built.Activities.Count(a => a.Id == "harvest"));
        Assert.Equal(CookingPrerequisites, built.Activities.Single(a => a.Id == "cook").Prerequisites);
        Assert.Equal(5, built.Activities.Single(a => a.Id == "cook").Inputs.Sum(i => i.Units.Value));
        Assert.DoesNotContain(built.Activities.Single(a => a.Id == "cook").Inputs, i => i.Resource.Value == "StoveCapability");
        Assert.All(built.Allocations, a => Assert.StartsWith("observed/", a.Provenance));
        var observed = result.Graphs.Single(g => g.Activities.All(a => a.Id != "build"));
        Assert.DoesNotContain(observed.Activities, a => a.Id == "build");
        // Producer output at the field cannot teleport to the cooking requirement.
        var absentMovement = request with { Operations = EquatableArray.From(request.Operations.Where(o => o.Activity.Id != "carry-b")) };
        Assert.Empty(ForecastActivityComposition.ComposeActivityDependencies(absentMovement).Graphs);
    }

    [Fact]
    public void PartialAllocationMatchesTinyExhaustiveOracleAndInputOrderDoesNotChangeCoverage()
    {
        var request = Request([Op("first", [Need("ingredients", "Rice", 2, ["a", "b"])]),
            Op("second", [Need("ingredients", "Rice", 2, ["a", "b"])])], [Supply("a", "Rice", 3), Supply("b", "Rice", 3)], "first", "second");
        string Signature(ForecastCompositionGraph g) => string.Join(",", Consumers
            .Select(id => g.Allocations.Where(a => a.Operation == id && a.Source == "a").Sum(a => a.Units)));
        var expected = (from first in Enumerable.Range(0, 3) from second in Enumerable.Range(0, 3)
            where first + second <= 3 && 4 - first - second <= 3 select $"{first},{second}").Order().ToArray();
        var result = ForecastActivityComposition.ComposeActivityDependencies(request);
        Assert.False(result.Truncated);
        Assert.Equal(expected, result.Graphs.Select(Signature).Order());
        var reordered = ForecastActivityComposition.ComposeActivityDependencies(request with { Operations = EquatableArray.From(request.Operations.Reverse()),
            Observed = EquatableArray.From(request.Observed.Reverse()), Roots = EquatableArray.From(request.Roots.Reverse()) });
        Assert.Equal(result.Graphs.Select(Signature), reordered.Graphs.Select(Signature));
        Assert.Equal(result.Expansions, reordered.Expansions);
    }

    [Fact]
    public void UnknownEligibilityTimingAndLocationRefuseOnlyAffectedLinks()
    {
        var known = Supply("known", "Rice", 2);
        var unknown = Supply("unknown", "Rice", 2) with { AvailableAt = default };
        var request = Request([Op("eat", [Need("food", "Rice", 2, [known.Id, unknown.Id])])], [known, unknown], "eat");
        var result = ForecastActivityComposition.ComposeActivityDependencies(request);
        Assert.Single(result.Graphs);
        Assert.Contains(result.Findings, f => f.StartsWith("unknown_composition_link", StringComparison.Ordinal));
        foreach (var changed in new[] { known with { Location = "elsewhere" }, known with { AvailableAt = Known(101d), ExpiresAt = Known(200d) },
            known with { Units = Known(1) }, known with { Provenance = "" } })
            Assert.Empty(ForecastActivityComposition.ComposeActivityDependencies(request with { Observed = [changed, unknown] }).Graphs);
        Assert.Empty(ForecastActivityComposition.ComposeActivityDependencies(request with { Operations = [Op("eat", [Need("food", "Rice", 2, [known.Id]) with { Sources = default }])] }).Graphs);
    }

    [Fact]
    public void SameChoiceQuantityVariantsCannotExhaustTheBudgetBeforeTheSingleDeliveryThatSatisfiesTheRecipe()
    {
        // One stack, delivered as exactly one of 1..10 units (shared choice). Partitions across several variants
        // are dead on arrival; they must be pruned instead of enumerated ahead of the full-quantity delivery.
        var deliveries = Enumerable.Range(1, 10).Select(n => Op("deliver-" + n, supplies: [Supply("yield-" + n, "Rice", n, producer: "deliver-" + n)])
            with { ChoiceGroup = "stack-a" }).ToArray();
        var cook = Op("cook", [Need("rice", "Rice", 10, deliveries.SelectMany(d => d.Supplies).Select(s => s.Id).ToArray())]);
        var result = ForecastActivityComposition.ComposeActivityDependencies(Request([.. deliveries, cook], [], "cook") with { ExpansionBudget = 2048 });
        Assert.False(result.Truncated);
        var graph = Assert.Single(result.Graphs);
        Assert.Contains(graph.Activities, a => a.Id == "deliver-10");
        Assert.DoesNotContain(result.Findings, f => f.StartsWith("unsatisfied_composition_requirement", StringComparison.Ordinal));
    }

    [Fact]
    public void CircularProductionAndConflictingRealizationsCannotFundThemselves()
    {
        var a = Supply("a", "A", 1, producer: "make-a"); var b = Supply("b", "B", 1, producer: "make-b");
        var cycle = ForecastActivityComposition.ComposeActivityDependencies(Request([Op("make-a", [Need("b", "B", 1, [b.Id])], [a]),
            Op("make-b", [Need("a", "A", 1, [a.Id])], [b])], [], "make-a"));
        Assert.Empty(cycle.Graphs);
        Assert.Contains(cycle.Findings, f => f.StartsWith("cyclic_composition", StringComparison.Ordinal));
        var conflict = ForecastActivityComposition.ComposeActivityDependencies(Request([Op("make-a", supplies: [a]) with { ChoiceGroup = "same-physical-choice" },
            Op("make-b", supplies: [b]) with { ChoiceGroup = "same-physical-choice" },
            Op("use", [Need("a", "A", 1, [a.Id]), Need("b", "B", 1, [b.Id])])], [], "use"));
        Assert.Empty(conflict.Graphs);
        Assert.Contains(conflict.Findings, f => f.StartsWith("conflicting_composition_choice", StringComparison.Ordinal));
    }

    [Fact]
    public void ExhaustedBudgetPreservesValidatedIncumbentButNotChangedStock()
    {
        var request = Request([Op("use", [Need("food", "Rice", 2, ["a", "b"])])], [Supply("a", "Rice", 2), Supply("b", "Rice", 2)], "use");
        var incumbent = ForecastActivityComposition.ComposeActivityDependencies(request).Graphs[0];
        var bounded = ForecastActivityComposition.ComposeActivityDependencies(request with { ExpansionBudget = 0, Incumbent = incumbent });
        Assert.True(bounded.Truncated);
        Assert.Equal(0, bounded.Expansions); Assert.Single(bounded.Graphs);
        Assert.Equal(incumbent.Allocations, bounded.Graphs[0].Allocations);
        var changed = ForecastActivityComposition.ComposeActivityDependencies(request with { ExpansionBudget = 0, Incumbent = incumbent,
            Observed = EquatableArray.From(request.Observed.Select(s => s with { Units = Known(0) })) });
        Assert.Empty(changed.Graphs);
        Assert.Contains("invalid_composition_incumbent", changed.Findings);
    }

    [Fact]
    public void ReusableFacilityIsNotConsumedWhileExclusiveCapacityUsesExistingScheduler()
    {
        var facility = Supply("station", "Facility", 1, kind: ForecastRequirementKind.Exclusive);
        var result = ForecastActivityComposition.ComposeActivityDependencies(Request([Op("a", [Need("station", "Facility", 1, [facility.Id], kind: ForecastRequirementKind.Exclusive)]),
            Op("b", [Need("station", "Facility", 1, [facility.Id], kind: ForecastRequirementKind.Exclusive)])], [facility], "a", "b"));
        var graph = Assert.Single(result.Graphs);
        Assert.All(graph.Activities, a => Assert.Empty(a.Inputs));
        var problem = new ForecastActivityProblem
        {
            Activities = graph.Activities.ToArray(), Pawns = [new("p", "bench", Known<EquatableArray<ForecastTimeWindow>>([new(0, 100)]), Known(1d)),
                new("q", "bench", Known<EquatableArray<ForecastTimeWindow>>([new(0, 100)]), Known(1d))],
            Options = graph.Allocations.Select((a, i) => new ForecastActivityOption(a.Operation, i == 0 ? "p" : "q", Known(10d), Known(0d)) { Equipment = [a.Capacity] }).ToArray(),
            Equipment = [new("station", "bench", Known<EquatableArray<ForecastTimeWindow>>([new(0, 100)]))],
            Deadline = Known(100d), AlternativeLaborCost = Known(100d), ExpansionBudget = 4096,
        };
        var selected = Assert.IsType<ForecastActivityPlan>(ForecastActivities.CompareActivityPlans(problem).Selected);
        Assert.Equal(20, selected.CompletedAt);
        Assert.Empty(ForecastActivities.CompareActivityPlans(problem with { Deadline = Known(15d) }).Alternatives);
    }

    [Fact]
    public void PhysicalAliasAndUnsupportedProducerQuantityCannotDuplicateOutput()
    {
        var source = Supply("a", "Rice", 2);
        Assert.Throws<DomainException>(() => ForecastActivityComposition.ComposeActivityDependencies(Request([Op("use", [Need("food", "Rice", 3, ["a", "alias"])])],
            [source, source with { Id = "alias" }], "use")));
        var produced = Supply("crop", "Rice", 3, producer: "harvest");
        var operation = Op("harvest", supplies: [produced]);
        operation = operation with { Activity = operation.Activity with { Outputs = [operation.Activity.Outputs[0] with { Units = Known(2d) }] } };
        var result = ForecastActivityComposition.ComposeActivityDependencies(Request([operation, Op("eat", [Need("food", "Rice", 3, [produced.Id])])], [], "eat"));
        Assert.Empty(result.Graphs);
        Assert.Contains("unsupported_composition_yield:crop", result.Findings);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void NormalFoodGenerationComposesTwoNativeEligiblePartialStacksForOneRecipe(double existingMealFreshness)
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        var context = new RimGovernor.Contracts.Wire.Common.ObservationContext
            { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
        RimGovernor.Contracts.Wire.Observations.CoupledFoodConversion Conversion(string source) =>
            new(source, "pawn", "bench", "CookMeal", "Rice", 4, "Meal", 1, new(1, 1), new(2, 1), new(2, 1),
                Known(0d), Known(10d), Known(20d), Known(10d), 4, 2)
            { RequiresHarvest = false, Bill = "bill", WorkGiver = "Cook" };
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 4)]),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = new() { Tick = 100, Pawns = [new() { Pawn = "pawn", State = "eligible",
                    Needs = [new("food", Known(.2), Known(.001), Known(.3))] }] },
                Coupled = new() { Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt, Clothing = ClothingFixtures.NoClothing, Tick = 100, Food = new(),
                    FoodSources = [new("a", "Rice", new(1, 1), 2, Known(1000d)), new("b", "Rice", new(1, 1), 2, Known(1000d)), new("unused-unknown", "Rice", new(9, 9), 2, default),
                        new("unrelated-meal", "Meal", new(9, 9), 1, existingMealFreshness < 0 ? default : Known(existingMealFreshness))],
                    FoodConversions = [Conversion("a"), Conversion("b")],
                    Construction = ConstructionFixtures.NoConstruction() with { Context = context,
                        Options = [new Row("shared-stock-frame", "TestWall", new(3, 1), "pawn", "Rice", "a", new(1, 1), 2, 4, "Finish",
                            Known(0d), Known(10d), Known(10d)) { AvailableInput = Known(2d), SiteApproachTicks = Known(0d) }] },
                    FoodUses = [new("pawn", "Meal", new(2, 1), Known(true), Known(.8), Known(10d), 1, Known(1000d))] },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300));
        var recipes = generated.Context.Activities.Activities.Where(t => t.Id.Contains("/recipe/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(recipes);
        Assert.Contains(generated.Alternatives, c => c.ExclusiveOutcome == "construction/shared-stock-frame");
        Assert.Contains(recipes, recipe => recipe.Inputs.Count == 2 && recipe.Inputs.All(i => i.Units == Known(2d))
            && recipe.Inputs.Select(i => i.Lot).Order().SequenceEqual(IngredientLots));
        Assert.All(recipes, recipe => Assert.Equal(4, recipe.Inputs.Sum(i => i.Units.Value)));
        var missingEligibility = facts with { ForecastObserved = facts.ForecastObserved with
            { Coupled = facts.ForecastObserved.Coupled! with { FoodConversions = [Conversion("a")] } } };
        var refused = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(missingEligibility, 300));
        Assert.DoesNotContain(refused.Context.Activities.Activities, t => t.Id.Contains("/recipe/", StringComparison.Ordinal));
        var unknownIngredient = facts with { ForecastObserved = facts.ForecastObserved with
            { Coupled = facts.ForecastObserved.Coupled! with { FoodSources = EquatableArray.From(facts.ForecastObserved.Coupled.FoodSources
                .Select(source => source.Target == "a" ? source with { FreshTicks = default } : source)) } } };
        var unknown = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(unknownIngredient, 300));
        Assert.DoesNotContain(unknown.Context.Activities.Activities, task => task.Id.Contains("/recipe/", StringComparison.Ordinal));
        var absentIngredient = facts with { ForecastObserved = facts.ForecastObserved with
            { Coupled = facts.ForecastObserved.Coupled! with { FoodSources = EquatableArray.From(facts.ForecastObserved.Coupled.FoodSources
                .Where(source => source.Target != "a")) } } };
        var absent = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(absentIngredient, 300));
        Assert.DoesNotContain(absent.Context.Activities.Activities, task => task.Id.Contains("/recipe/", StringComparison.Ordinal));
    }

    [Fact]
    public void DemandOnlyClosurePreservesEveryPhysicalSiteRealizationWithoutInventingWork()
    {
        var cheap = Supply("site-a", "CompleteSite", 1, producer: "finish-a", kind: ForecastRequirementKind.Reusable);
        var other = Supply("site-b", "CompleteSite", 1, producer: "finish-b", kind: ForecastRequirementKind.Reusable);
        var site = Need("shell", "CompleteSite", 1, [cheap.Id, other.Id], kind: ForecastRequirementKind.Reusable);
        var roof = Supply("roof", "CompleteRoof", 1, producer: "roof-work", kind: ForecastRequirementKind.Reusable);
        var request = Request([Op("finish-a", supplies: [cheap]) with { ChoiceGroup = "physical-site" },
            Op("finish-b", supplies: [other]) with { ChoiceGroup = "physical-site" }, Op("roof-work", [site], [roof])], []) with
        { RootRequirements = [site, Need("roof", "CompleteRoof", 1, [roof.Id], kind: ForecastRequirementKind.Reusable)] };
        var result = ForecastActivityComposition.ComposeActivityDependencies(request);
        Assert.Equal(2, result.Graphs.Count);
        Assert.All(result.Graphs, graph =>
        {
            Assert.Equal(2, graph.Activities.Count);
            Assert.DoesNotContain(graph.Activities, a => a.Id.Length == 0);
            Assert.Single(graph.Activities, a => a.Id.StartsWith("finish-", StringComparison.Ordinal));
            Assert.Equal(2, graph.Allocations.Count(a => a.Operation.Length == 0));
        });
        var retained = ForecastActivityComposition.ComposeActivityDependencies(request with { ExpansionBudget = 0, Incumbent = result.Graphs[1] });
        var retainedGraph = Assert.Single(retained.Graphs);
        Assert.Equal(result.Graphs[1].Allocations, retainedGraph.Allocations);
        Assert.Equal(result.Graphs[1].Activities.Select(a => a.Id), retainedGraph.Activities.Select(a => a.Id));
        Assert.All(retainedGraph.Activities, a => Assert.Equal(result.Graphs[1].Activities.Single(original => original.Id == a.Id).Prerequisites, a.Prerequisites));
    }

    [Fact]
    public void FacilityExpiryBoundsServiceFinishAndDepthLimitReportsIncompleteSearch()
    {
        var facility = Supply("facility", "Facility", 1, kind: ForecastRequirementKind.Reusable) with
            { AvailableAt = Known(5d), ExpiresAt = Known(12d) };
        var request = Request([Op("use", [Need("facility", "Facility", 1, [facility.Id], kind: ForecastRequirementKind.Reusable)])], [facility], "use");
        var activity = Assert.Single(Assert.Single(ForecastActivityComposition.ComposeActivityDependencies(request).Graphs).Activities);
        Assert.Equal(5, activity.NotBefore); Assert.Equal(12, activity.LatestStart); Assert.Equal(12, activity.LatestFinish);
        var produced = facility with { Producer = "build", AvailableAt = Known(0d) };
        var chain = request with { Operations = [Op("build", supplies: [produced]), request.Operations[0]], Observed = [], DepthLimit = 1 };
        var bounded = ForecastActivityComposition.ComposeActivityDependencies(chain);
        Assert.Empty(bounded.Graphs); Assert.True(bounded.Truncated);
        Assert.Contains("composition_depth_or_size_bound", bounded.Findings);
    }
    [Fact]
    public void NormalShelterGenerationKeepsBothNativeMaterialRoutesThroughRoofComposition()
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var context = new ObservationContext { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
        var inside = new Cell(1, 1); var wall = new Cell(0, 1);
        var cells = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Cell(x, z))).ToArray();
        ConstructionActivityOption Option(string source) => new Row("wall", "TestWall", wall, "pawn", "Rice", source, wall, 1, 10, "Finish",
            Known(0d), Known(10d), Known(10d)) { AvailableInput = Known(1d), SiteApproachTicks = Known(0d) };
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 2)]),
            PlannedShelters = [new() { Id = 1, Region = RegionRules.RectRegion(new(1, 1, 1, 1)) }],
            ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
            {
                Food = new(), FoodUses = [],
                Construction = ConstructionFixtures.NoConstruction() with { Context = context, Options = [Option("a"), Option("b")] },
                Thermal = CoupledThermalTests.NoThermal with
                {
                    Outdoors = -10d, ThinRoofExchangePerTick = .00005, WallExchangePerTick = .000017,
                    Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, -10d, c == inside ? "none" : "thin",
                        c != inside && c != wall, false, c == wall ? "wall" : "",
                        c == wall, false, 0, c == inside))),
                    Workers = [new("pawn", 5d)], RoofRoutes = [new("pawn", inside, new RoofApproach.Path(1d))],
                    Sources = [new("heater", inside, true, .35, 20d, CoupledThermalTests.Powered(1000d))],
                },
            } },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var generated = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300));
        Assert.Equal(2, generated.Alternatives.Count(c => c.ExclusiveOutcome == "shelter/planned/1"));
        var roof = generated.Context.Activities.Activities.Where(a => a.Id.StartsWith("roof/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, roof.Length);
        Assert.All(roof, task => Assert.Single(task.Prerequisites));
        Assert.Equal(2, roof.SelectMany(t => t.Prerequisites).Distinct().Count());
        var plan = Assert.Single(generated.Context.Shelters);
        Assert.All(plan.AlternativePrerequisites, group => Assert.Equal(2, group.Count));
    }
}
