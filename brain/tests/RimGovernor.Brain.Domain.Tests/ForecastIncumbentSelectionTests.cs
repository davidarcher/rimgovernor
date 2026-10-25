using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastIncumbentSelectionTests
{
    private static (ColonyForecastRequest Request, ForecastCoupledCandidates Candidates) Problem(double food, double fall, bool meal = true)
    {
        var facts = ForecastCoupledSelectionTests.Review(food, fall);
        facts = WorkRules.ReviewWorkResources(facts with { Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 1)]) },
            Known<EquatableArray<ProductionBatch>>([]), new([]));
        var original = facts.CoupledAlternatives!;
        var pawn = original.Context.Needs.Pawns[0] with
        {
            Needs = [NeedsFixtures.Rate("food", food, fall, .3),
                NeedsFixtures.Rate("rest", 1d, 0d, .3),
                NeedsFixtures.Rate("joy", .5, 0d, .3)]
        };
        ForecastCommitment current = new("current-joy", []) { ActivityCompletions = ["ongoing-joy"] };
        ForecastCommitment eat = new("eat", []) { ActivityCompletions = ["meal"] };
        var context = original.Context with
        {
            Resources = facts.WorkView!,
            Activities = new()
            {
                Activities = [new("ongoing-joy", "start", "start", []) { LatestStart = 0 },
                    new("meal", "start", "start", []) { Inputs = [new(new("Rice"), 1d) { Lot = "rice", LotLocation = "start" }] }],
                Pawns = [new("pawn", "start", new ForecastCapacityWindows.Read([new(0, 300)]), 1d)],
                Options = [new("ongoing-joy", "pawn", 250d, 0d), new("meal", "pawn", 10d, 0d)],
                Deadline = 300d, ExpansionBudget = 4096
            },
            Needs = original.Context.Needs with { Pawns = [pawn] },
            InitialLots = [new("rice", "start", new("Rice"), Known(1d)) { Perishable = true, FreshTicks = Known(1000d) }],
            NeedTasks = [new("ongoing-joy", "pawn", "joy", "horseshoes", new(""), 0, .25, []),
                new("meal", "pawn", "food", "rice", new("Rice"), 1, .8, [])
                { Quantity = new TargetQuantity.Units(1) }],
            // What the synthetic incumbent holds, as PrepareCoupledIncumbents sets it: its commitment, its ongoing service and its
            // pawn, none dispatched now (#3889).
            IncumbentHold = new(["current-joy"], ["ongoing-joy"], ["pawn"], []),
        };
        var candidates = new ForecastCoupledCandidates(context, meal ? [eat] : [], [], false) { Incumbents = [current] };
        facts = facts with { CoupledAlternatives = candidates };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        return (request, candidates);
    }

    [Fact]
    public void RetainedServiceCanPrecedeLaterServiceOfTheSameKind()
    {
        var (request, candidates) = Problem(1, 0, meal: false);
        var context = candidates.Context;
        var pawn = context.Needs.Pawns[0];
        context = context with
        {
            Needs = context.Needs with { Pawns = [pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n =>
                n.Kind == "joy" ? n with { Level = .1, FallPerTick = .001 } : n)) }] },
            NeedTasks = [.. context.NeedTasks, new("future-joy", "pawn", "joy", "horseshoes", new(""), 0, .25, [])],
            Activities = context.Activities with
            {
                Activities = [.. context.Activities.Activities, new("future-joy", "start", "start", []) { NotBefore = 100 }],
                Options = [.. context.Activities.Options.Select(o => o.Activity == "ongoing-joy" ? o with { WorkTicks = 100d } : o),
                    new("future-joy", "pawn", 100d, 0d)]
            }
        };
        candidates = candidates with { Context = context, Alternatives = [new("future-joy", []) { ActivityCompletions = ["future-joy"] }] };
        var result = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Contains("current-joy", result.Selection.Selected);
        Assert.Contains("future-joy", result.Selection.Selected);
        var current = Assert.Single(result.Selected.Plan.Allocations, a => a.Activity == "ongoing-joy");
        var later = Assert.Single(result.Selected.Plan.Allocations, a => a.Activity == "future-joy");
        Assert.Equal(0, current.Start);
        Assert.True(later.Start >= current.End);
    }
    [Fact]
    public void SyntheticRetainedTargetCannotBeAssignedToAnotherParticipant()
    {
        var (request, candidates) = Problem(1, 0, meal: false);
        var roster = WorkResourceTestFacts.Create("pawn", "helper");
        roster = WorkRules.ReviewWorkResources(roster, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var context = candidates.Context;
        context = context with
        {
            Resources = roster.WorkView!,
            Needs = context.Needs with { Pawns = [context.Needs.Pawns[0], context.Needs.Pawns[0] with { Pawn = "helper" }] },
            NeedTasks = [.. context.NeedTasks, new("helper-joy", "helper", "joy", "horseshoes", new(""), 0, .2, [])],
            Activities = context.Activities with
            {
                Activities = [.. context.Activities.Activities, new("helper-joy", "start", "start", [])],
                Options = [.. context.Activities.Options, new("helper-joy", "helper", 20d, 0d)],
                Pawns = [.. context.Activities.Pawns, new("helper", "start", new ForecastCapacityWindows.Read([new(0, 300)]), 1d)]
            }
        };
        var helper = new ForecastCommitment("helper-joy", []) { ActivityCompletions = ["helper-joy"] };
        // No native dispatch bindings exist for the synthetic incumbent. The exact need target
        // must still prevent a second owner, even when native participant capacity allows it.
        var forecast = CoupledForecastKit.Forecast(request with { Coupled = context,
            Commitments = candidates.Incumbents }, [helper]);
        Assert.DoesNotContain(forecast.Proposed.Coupled!.Alternatives, a => a.Feasible == Known(true));
        Assert.Contains(forecast.Proposed.Coupled.Alternatives.SelectMany(a => a.Findings),
            f => f.Reason == "conflicting_selected_target_owners" && f.Commitment == "horseshoes");
    }
    /// <summary>The retained pawn plus <paramref name="helpers"/> proposed ones claim one target of the given capacity and stack.</summary>
    private static IReadOnlyList<ForecastFinding> ClaimsOnOneTarget(string kind, string target, int maxPawns, int helpers,
        out bool anyFeasible, int count = 0, TargetQuantity? stock = null)
    {
        var (request, candidates) = Problem(1, 0, meal: false);
        var names = Enumerable.Range(1, helpers).Select(i => "helper" + i).ToArray();
        var roster = WorkRules.ReviewWorkResources(WorkResourceTestFacts.Create(["pawn", .. names]), Known<EquatableArray<ProductionBatch>>([]), new([]));
        var context = candidates.Context;
        ForecastNeedTask Claim(string task, string pawn) => new(task, pawn, kind, target, new(""), count, .2, [])
            { MaxPawns = maxPawns, Quantity = stock ?? new TargetQuantity.Unbounded() };
        // Each helper's service is bound, so the binding starts it (#3889).
        var bindings = context.Bindings.ToDictionary(b => b.Key, b => b.Value);
        foreach (var n in names)
            bindings[(n + "-joy", n)] = new(n + "-joy", n, [], PawnActivityKind.Recreation, target, "", "", count, 2000, new(0, 0));
        context = context with
        {
            Resources = roster.WorkView!, Bindings = bindings,
            Needs = context.Needs with { Pawns = [context.Needs.Pawns[0], .. names.Select(n => context.Needs.Pawns[0] with { Pawn = n })] },
            NeedTasks = [Claim("ongoing-joy", "pawn"), .. names.Select(n => Claim(n + "-joy", n))],
            Activities = context.Activities with
            {
                Activities = [.. context.Activities.Activities, .. names.Select(n => new ForecastActivity(n + "-joy", "start", "start", []))],
                Options = [.. context.Activities.Options, .. names.Select(n => new ForecastActivityOption(n + "-joy", n, 20d, 0d))],
                Pawns = [.. context.Activities.Pawns, .. names.Select(n => new ForecastPawnCapacity(n, "start", new ForecastCapacityWindows.Read([new(0, 300)]), 1d))]
            }
        };
        var forecast = CoupledForecastKit.Forecast(request with { Coupled = context, Commitments = candidates.Incumbents },
            [.. names.Select(n => new ForecastCommitment(n + "-joy", []) { ActivityCompletions = [n + "-joy"] })]);
        anyFeasible = forecast.Proposed.Coupled!.Alternatives.Any(a => a.Feasible == Known(true));
        return [.. forecast.Proposed.Coupled.Alternatives.SelectMany(a => a.Findings)];
    }

    [Theory]
    // #3626: "helper" hauls four rice from the stack "pawn" eats two of. Hauled first and done before the eat departs (the
    // pawn works until then), the forecast selects it and admission admits the same orders; at once, both refuse it, by one rule.
    [InlineData(50d, true)]    [InlineData(0d, false)]
    public void TheForecastAndAdmissionAgreeOnAHaulAndAnEatFromOneStack(double eatStart, bool fits)
    {
        var (request, candidates) = Problem(1, 0, meal: false);
        var rice = NeedsFixtures.Food("rice", "Rice", 2, 0d, 10d, 0d) with { Stock = new NeedStock.Stacked(20, new FoodFreshness.Never()), MaxPawns = 10 };
        var facts = WorkResourceTestFacts.Create("pawn", "helper");
        facts = facts with { ForecastObserved = facts.ForecastObserved with
            { Needs = NeedsFixtures.Empty with { Tick = new(100), Pawns = [NeedsFixtures.Pawn() with { Pawn = "pawn", Targets = [rice] }, NeedsFixtures.Pawn() with { Pawn = "helper", Targets = [rice] }] } } };
        var view = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])).WorkView!;
        var bindings = new Dictionary<(string, string), RimGovernor.Brain.Policy.Methods.ActivityTaskCandidate>
        {
            [("eat", "pawn")] = new("eat", "pawn", [], PawnActivityKind.Eat, "rice", "", "", 2, 2000, new(1, 1)) { Resource = "Rice", Source = new(1, 1) },
            [("haul", "helper")] = new("haul", "helper", [], PawnActivityKind.Deliver, "rice", "", "", 4, 2000, new(1, 1))
                { Resource = "Rice", Source = new(1, 1), Destination = new(2, 1), Capacity = 4 },
        };
        var context = candidates.Context with
        {
            Resources = view, Bindings = bindings,
            Needs = candidates.Context.Needs with { Pawns = [candidates.Context.Needs.Pawns[0] with { Targets = [rice] },
                candidates.Context.Needs.Pawns[0] with { Pawn = "helper", Targets = [rice] }] },
            InitialLots = [new("rice", "start", new("Rice"), Known(20d)) { Perishable = true, FreshTicks = Known(1000d) }],
            NeedTasks = [new("eat", "pawn", "food", "rice", new("Rice"), 2, .1, []) { Quantity = new TargetQuantity.Units(20), MaxPawns = 10 }],
            Activities = candidates.Context.Activities with
            {
                Activities = [new("haul", "start", "start", []) { LatestStart = 0 }, new("work", "start", "start", []) { LatestStart = 0 },
                    new("eat", "start", "start", []) { NotBefore = eatStart, LatestStart = eatStart, Inputs = [new(new("Rice"), 2d) { Lot = "rice", LotLocation = "start" }] }],
                Options = [new("haul", "helper", 20d, 0d), new("work", "pawn", Math.Max(eatStart, 1d), 0d), new("eat", "pawn", 20d, 0d)],
                Pawns = [.. candidates.Context.Activities.Pawns, new("helper", "start", new ForecastCapacityWindows.Read([new(0, 300)]), 1d)],
            },
        };
        var both = new ForecastCommitment("both", []) { ActivityCompletions = eatStart > 0 ? ["haul", "work", "eat"] : ["haul", "eat"] };
        var forecast = CoupledForecastKit.Forecast(request with { Coupled = context, Commitments = [] }, [both]);
        var outcomes = forecast.Proposed.Coupled!.Alternatives;
        var findings = string.Join(",", outcomes.SelectMany(a => a.Findings).Select(f => f.Reason + ":" + f.Commitment));
        Assert.True(fits == outcomes.Any(a => a.Feasible == Known(true)), findings);
        Assert.True(!fits == findings.Contains("conflicting_selected_target_owners:rice", StringComparison.Ordinal), findings);
        if (fits) Assert.Equal(eatStart, outcomes.First(a => a.Feasible == Known(true)).Plan.Allocations.Single(a => a.Activity == "eat").Depart);

        // Admission of the selected schedule's haul and eat orders, or of both at once when nothing was selected.
        var plan = fits ? outcomes.First(a => a.Feasible == Known(true)).Plan
            : new ForecastActivityPlan([new("haul", "helper", 0, 0, 0, 20, 0, 0, 20, 0, 0), new("eat", "pawn", 0, 0, 0, 20, 0, 0, 20, 0, 0)], 20, 40);
        plan = plan with { Allocations = plan.Allocations.Where(a => a.Activity != "work").ToArray() };
        var batch = RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.Start(plan, bindings, 1, "coupled", 100);
        var refusal = WorkRules.WorkResourcesRefusal(view, new(new("both"), new(1), [DomainAction.NewPawnActivitiesAction(new("both-action"), batch)]));
        Assert.Equal(fits ? null : "shared_target:rice", refusal);
    }

    [Fact]
    public void TwoPawnsCannotClaimAOneSlotBed()
    {
        var findings = ClaimsOnOneTarget("rest", "bed", 1, 1, out var feasible);
        Assert.False(feasible);
        Assert.Contains(findings, f => f.Reason == "conflicting_selected_target_owners" && f.Commitment == "bed");
    }

    [Fact]
    public void APinAdmitsUpToItsParticipantsAndRefusesOneMore()
    {
        var within = ClaimsOnOneTarget("joy", "pin", 3, 2, out var feasibleWithin);
        Assert.True(feasibleWithin);
        Assert.DoesNotContain(within, f => f.Reason is "conflicting_selected_target_owners" or "target_stack_quantity_exceeded");
        var over = ClaimsOnOneTarget("joy", "pin", 3, 3, out var feasibleOver);
        Assert.False(feasibleOver);
        Assert.Contains(over, f => f.Reason == "conflicting_selected_target_owners" && f.Commitment == "pin");
    }

    [Fact]
    public void ConsumersSharingAStackFitWithinItsQuantityAndAreRefusedBeyondIt()
    {
        // Three claimants of two units each against a stack of ten (capacity 10): six fits, then the stack is exhausted.
        var within = ClaimsOnOneTarget("joy", "stack", 10, 2, out var feasibleWithin, count: 2, stock: new TargetQuantity.Units(6));
        Assert.True(feasibleWithin);
        Assert.DoesNotContain(within, f => f.Reason == "target_stack_quantity_exceeded");
        var beyond = ClaimsOnOneTarget("joy", "stack", 10, 2, out var feasibleBeyond, count: 2, stock: new TargetQuantity.Units(5));
        Assert.False(feasibleBeyond);
        Assert.Contains(beyond, f => f.Reason == "target_stack_quantity_exceeded" && f.Commitment == "stack");
        Assert.DoesNotContain(beyond, f => f.Reason == "conflicting_selected_target_owners");
    }
    [Fact]
    public void NewlyUrgentFoodCanReleaseCurrentJoyThroughTheSameConsequenceComparison()
    {
        var (request, candidates) = Problem(.1, .001);
        var result = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Equal(Known(true), result.Baseline.Feasible);
        Assert.Equal("ongoing-joy", Assert.Single(result.Baseline.Plan.Allocations).Activity);
        Assert.Contains("eat", result.Selection.Selected);
        Assert.DoesNotContain("current-joy", result.Selection.Selected);
        Assert.DoesNotContain(result.Selected.Plan.Allocations, a => a.Activity == "ongoing-joy");
        Assert.True(result.SelectedVector.Urgent[0].Value < result.BaselineVector.Urgent[0].Value);
        Assert.Contains(result.Rejected, p => p.Commitments.Count == 0);
    }

    [Fact]
    public void EqualConsequencesRetainCurrentServiceDespiteLowerEmptyPortfolioLabor()
    {
        var (request, candidates) = Problem(1, 0, meal: false);
        var result = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        Assert.Equal("current-joy", Assert.Single(result.Selection.Selected));
        Assert.Equal(result.BaselineVector, result.SelectedVector);
        var empty = Assert.Single(result.Rejected, p => p.Commitments.Count == 0);
        Assert.NotNull(empty.Consequences);
        Assert.True(empty.Consequences.Labor < result.BaselineVector.Labor);
        // The tie reached the ledger, so the loser's vector is complete, and the incumbent rule is the stage it lost at (#3723).
        Assert.Equal(ForecastCoupledSelection.IncumbentStage, ForecastPortfolioEvidence.StageOf(empty.Reason));
        Assert.Throws<DomainException>(() => ForecastCoupledSelection.StartCoupledActivity(result, candidates, 1, "replacement", 100));
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(.1, .001, true)]
    public void TheLazySearchRetainsOrReleasesTheIncumbentExactlyAsTheEagerOne(double food, double fall, bool meal)
    {
        // #3723: the incumbent tiebreak and the floor that releases the incumbent decide the same under lazy and eager evaluation.
        var (request, candidates) = Problem(food, fall, meal);
        var lazy = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        var eager = ForecastCoupledSelection.CompareActivityOutcomesEagerly(request, candidates);
        LazyEvaluationTests.AssertPaired(eager, lazy);
        Assert.Contains(lazy.Rejected, r => ForecastPortfolioEvidence.StageOf(r.Reason) == ForecastCoupledSelection.IncumbentStage && r.Consequences is not null);
    }

    [Fact]
    public void ExhaustedOptionalSearchKeepsBaselineAndReportsItsBoundDeterministically()
    {
        var (request, candidates) = Problem(.1, .001);
        var first = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates, new(0));
        var second = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates, new(0));
        Assert.Equal("current-joy", Assert.Single(first.Selection.Selected));
        Assert.True(first.Selection.SearchTruncated);
        Assert.Contains("portfolio_search_truncated", first.Selection.Findings);
        // A zero budget evaluates the mandatory baseline, which is reported but charged to no allowance, and nothing else (#3722).
        Assert.Contains(ForecastCoupledSelection.OutcomeEvaluationsExhausted, first.Selection.Findings);
        Assert.True(first.BaselineEvaluations > 0);
        Assert.Equal(first.BaselineEvaluations, first.OutcomeEvaluations);
        Assert.Equal(first.BaselineExpansions, first.Selection.Expansions);
        Assert.Equal(first.OutcomeEvaluations, second.OutcomeEvaluations);
        Assert.Equal(first.Selection.Selected, second.Selection.Selected);
        Assert.Equal(first.Selection.Expansions, second.Selection.Expansions);
        Assert.Equal(first.SelectedVector, second.SelectedVector);
    }

    [Fact]
    public void IncumbentsAndNewAlternativesCannotReuseCandidateIdentity()
    {
        var (request, candidates) = Problem(.1, .001);
        candidates = candidates with { Alternatives = candidates.Incumbents };
        Assert.Throws<DomainException>(() => ForecastCoupledSelection.CompareActivityOutcomes(request, candidates));
    }
    [Fact]
    public void WaitingClassificationCannotBypassNeedServiceFeasibility()
    {
        var (request, candidates) = Problem(.1, .001);
        candidates = candidates with
        {
            Context = candidates.Context with { NonproductiveOccupancy = ["ongoing-joy"] }
        };
        Assert.Throws<DomainException>(() => ForecastCoupledSelection.CompareActivityOutcomes(request, candidates));
    }

    /// <summary>A chore two pawns can do: the scheduler tries the cheaper one first, whom the needs timeline then shows unavailable all day
    /// (<c>work_outside_recovered_capacity</c>), so the schedule search runs again without that pawn's choice (the capacity-recovery retry).</summary>
    private static (ColonyForecastRequest Request, IReadOnlyDictionary<string, ForecastCommitment> Commitments) Chore()
    {
        var (request, candidates) = Problem(1, 0, meal: false);
        var facts = WorkResourceTestFacts.Create("pawn", "helper");
        facts = facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = new WorkPawnsRead.Read(EquatableArray.From(((WorkPawnsRead.Read)facts.ForecastObserved.WorkPawns).Pawns
            .Select(p => p.Id.Value == "helper" ? p with { Available = false } : p))) } };
        var roster = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var context = candidates.Context;
        context = context with
        {
            Resources = roster.WorkView!,
            Needs = context.Needs with { Pawns = [context.Needs.Pawns[0], context.Needs.Pawns[0] with { Pawn = "helper" }] },
            NeedTasks = [],
            Activities = context.Activities with
            {
                Activities = [new("chore", "start", "start", [])],
                Options = [new("chore", "helper", 20d, 0d), new("chore", "pawn", 20d, 0d)],
                Pawns = [new("helper", "start", new ForecastCapacityWindows.Read([new(0, 300)]), .5),
                    new("pawn", "start", new ForecastCapacityWindows.Read([new(0, 300)]), 1d)],
            },
        };
        return (request with { Coupled = context }, ColonyForecastRules.ForecastCommitmentsById([new ForecastCommitment("chore", []) { ActivityCompletions = ["chore"] }]));
    }

    /// <summary>#3722: the capacity-recovery retries of one schedule search draw on the comparison's ledger as its first search does: every
    /// retry's expansions and evaluations are counted, and no retry starts once an allowance is spent.</summary>
    [Fact]
    public void TheCapacityRecoveryRetryIsAccountedAndStopsWhenAnAllowanceEnds()
    {
        // Two expansions reach the cheaper helper's plan and no other; the helper's capacity window is empty, so the plan is infeasible
        // and the retry without the helper reaches the pawn's.
        var (request, commitments) = Chore();
        const int bound = 2;
        var ledger = new ForecastSearchLedger(new(8, Traversal: 8));
        var free = ForecastCoupled.ForecastCoupledScenarioOf(request, commitments, new(), ledger, bound).Coupled!;
        var why = string.Join(";", free.Findings) + " | " + string.Join(";", free.Alternatives.Select(a => string.Join(",", a.Plan.Allocations.Select(x => x.Pawn)) + ":" + a.Feasible + ":" + string.Join("+", a.Findings.Select(f => f.Reason))));
        Assert.True(free.Alternatives.Count == 2, why);
        var retried = Assert.Single(free.Alternatives, a => a.Findings.Any(f => f.Reason == "work_outside_recovered_capacity"));
        Assert.True(retried.Plan.Allocations.Length == 1 && retried.Plan.Allocations[0].Pawn == "helper", why);
        var served = Assert.Single(free.Alternatives, a => a.Feasible == Known(true));
        Assert.True(served.Plan.Allocations.Length == 1 && served.Plan.Allocations[0].Pawn == "pawn", why);
        // Both schedule searches' expansions and both plans' evaluations are on the ledger, none of them as baseline work; the same
        // search spends the same units again. The two-expansion schedule searches are cut by their own bound; neither allowance ended.
        Assert.True(ledger.Expansions == 2 * bound, why + " expansions=" + ledger.Expansions);
        Assert.Equal((2, 0, 0), (ledger.Evaluations, ledger.BaselineEvaluations, ledger.BaselineExpansions));
        var again = new ForecastSearchLedger(new(8, Traversal: 8));
        var counted = ForecastCoupled.ForecastCoupledScenarioOf(request, commitments, new(), again, bound).Coupled!;
        Assert.Equal(free.Alternatives.Select(a => (a.Feasible, a.Plan.Allocations[0].Pawn)), counted.Alternatives.Select(a => (a.Feasible, a.Plan.Allocations[0].Pawn)));
        Assert.Equal((ledger.Expansions, ledger.Evaluations), (again.Expansions, again.Evaluations));
        Assert.True(counted.Truncated && free.Truncated);
        Assert.DoesNotContain(ForecastCoupledSelection.OutcomeEvaluationsExhausted, counted.Findings);
        Assert.DoesNotContain(ForecastCoupledSelection.TraversalBoundExhausted, counted.Findings);

        // One evaluation: the first search's plan is evaluated, the retry's schedule search does not start, and the scenario says why
        // it is incomplete.
        var one = new ForecastSearchLedger(new(1, Traversal: 8));
        var starved = ForecastCoupled.ForecastCoupledScenarioOf(request, commitments, new(), one, bound).Coupled!;
        Assert.Equal("helper", Assert.Single(starved.Alternatives).Plan.Allocations[0].Pawn);
        Assert.Equal((1, bound), (one.Evaluations, one.Expansions));
        Assert.True(starved.Truncated);
        Assert.Contains(ForecastCoupledSelection.OutcomeEvaluationsExhausted, starved.Findings);
        Assert.DoesNotContain(starved.Alternatives, a => a.Feasible == Known(true));

        // A traversal bound the first search spends: its plan is evaluated, the retry's schedule search gets no expansions and is not run.
        var cut = new ForecastSearchLedger(new(8, Traversal: bound));
        var stopped = ForecastCoupled.ForecastCoupledScenarioOf(request, commitments, new(), cut, bound).Coupled!;
        Assert.Equal("helper", Assert.Single(stopped.Alternatives).Plan.Allocations[0].Pawn);
        Assert.Equal((1, bound), (cut.Evaluations, cut.Expansions));
        Assert.True(stopped.Truncated);
        Assert.Contains(ForecastCoupledSelection.TraversalBoundExhausted, stopped.Findings);

        // A traversal bound below the first search's: the search is cut before any plan, nothing is evaluated, and the bound's end is
        // reported.
        var below = new ForecastSearchLedger(new(8, Traversal: 1));
        var none = ForecastCoupled.ForecastCoupledScenarioOf(request, commitments, new(), below, bound).Coupled!;
        Assert.Empty(none.Alternatives);
        Assert.Equal((0, 1), (below.Evaluations, below.Expansions));
        Assert.True(none.Truncated);
        Assert.Contains(ForecastCoupledSelection.TraversalBoundExhausted, none.Findings);

        // Mandatory work ignores both allowances and is charged to the baseline.
        var mandatory = new ForecastSearchLedger(new(0, Traversal: 0)) { Mandatory = true };
        var baseline = ForecastCoupled.ForecastCoupledScenarioOf(request, commitments, new(), mandatory, bound).Coupled!;
        Assert.Equal(2, baseline.Alternatives.Count);
        Assert.Equal((2, ledger.Expansions, 2, ledger.Expansions), (mandatory.Evaluations, mandatory.Expansions, mandatory.BaselineEvaluations, mandatory.BaselineExpansions));
        Assert.DoesNotContain(ForecastCoupledSelection.OutcomeEvaluationsExhausted, baseline.Findings);
        Assert.DoesNotContain(ForecastCoupledSelection.TraversalBoundExhausted, baseline.Findings);
    }
}
