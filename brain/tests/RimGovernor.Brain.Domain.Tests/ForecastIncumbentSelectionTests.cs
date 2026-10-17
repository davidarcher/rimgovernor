using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastIncumbentSelectionTests
{
    private static (ColonyForecastRequest Request, ForecastCoupledCandidates Candidates) Problem(double food, double fall, bool meal = true)
    {
        var facts = ForecastCoupledSelectionTests.Review(food, fall);
        facts = ReviewWorkResources(facts with { Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 1)]) },
            Known<EquatableArray<ProductionBatch>>([]), new([]));
        var original = facts.CoupledAlternatives!;
        var pawn = original.Context.Needs.Pawns[0] with
        {
            Needs = [new("food", Known(food), Known(fall), Known(.3)),
                new("rest", Known(1d), Known(0d), Known(.3)),
                new("joy", Known(.5), Known(0d), Known(.3))]
        };
        ForecastCommitment current = new("current-joy", []) { ActivityCompletions = ["ongoing-joy"] };
        ForecastCommitment eat = new("eat", []) { ActivityCompletions = ["meal"] };
        var context = original.Context with
        {
            Resources = facts.WorkView!,
            Activities = new()
            {
                Activities = [new("ongoing-joy", "start", "start", []) { LatestStart = 0 },
                    new("meal", "start", "start", []) { Inputs = [new(new("Rice"), Known(1d)) { Lot = "rice", LotLocation = "start" }] }],
                Pawns = [new("pawn", "start", Known<EquatableArray<ForecastTimeWindow>>([new(0, 300)]), Known(1d))],
                Options = [new("ongoing-joy", "pawn", Known(250d), Known(0d)), new("meal", "pawn", Known(10d), Known(0d))],
                Deadline = Known(300d), ExpansionBudget = 4096
            },
            Needs = original.Context.Needs with { Pawns = [pawn] },
            InitialLots = [new("rice", "start", new("Rice"), Known(1d)) { Perishable = true, FreshTicks = Known(1000d) }],
            NeedTasks = [new("ongoing-joy", "pawn", "joy", "horseshoes", new(""), 0, Known(.25), []),
                new("meal", "pawn", "food", "rice", new("Rice"), 1, Known(.8), [])
                { AvailableCount = Known(1d), FreshTicks = Known(1000d) }]
        };
        var candidates = new ForecastCoupledCandidates(context, meal ? [eat] : [], [], false) { Incumbents = [current] };
        facts = facts with { CoupledAlternatives = candidates };
        var request = CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputsOf(facts) }, [], new());
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
                n.Kind == "joy" ? n with { Level = Known(.1), FallPerTick = Known(.001) } : n)) }] },
            NeedTasks = [.. context.NeedTasks, new("future-joy", "pawn", "joy", "horseshoes", new(""), 0, Known(.25), [])],
            Activities = context.Activities with
            {
                Activities = [.. context.Activities.Activities, new("future-joy", "start", "start", []) { NotBefore = 100 }],
                Options = [.. context.Activities.Options.Select(o => o.Activity == "ongoing-joy" ? o with { WorkTicks = Known(100d) } : o),
                    new("future-joy", "pawn", Known(100d), Known(0d))]
            }
        };
        candidates = candidates with { Context = context, Alternatives = [new("future-joy", []) { ActivityCompletions = ["future-joy"] }] };
        var result = CompareActivityOutcomes(request, candidates);
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
        roster = ReviewWorkResources(roster, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var context = candidates.Context;
        context = context with
        {
            Resources = roster.WorkView!,
            Needs = context.Needs with { Pawns = [context.Needs.Pawns[0], context.Needs.Pawns[0] with { Pawn = "helper" }] },
            NeedTasks = [.. context.NeedTasks, new("helper-joy", "helper", "joy", "horseshoes", new(""), 0, Known(.2), [])],
            Activities = context.Activities with
            {
                Activities = [.. context.Activities.Activities, new("helper-joy", "start", "start", [])],
                Options = [.. context.Activities.Options, new("helper-joy", "helper", Known(20d), Known(0d))],
                Pawns = [.. context.Activities.Pawns, new("helper", "start", Known<EquatableArray<ForecastTimeWindow>>([new(0, 300)]), Known(1d))]
            }
        };
        var helper = new ForecastCommitment("helper-joy", []) { ActivityCompletions = ["helper-joy"] };
        // No native dispatch bindings exist for the synthetic incumbent. The exact need target
        // must still prevent a second owner, even when native participant capacity allows it.
        var forecast = ForecastColony(request with { Coupled = context,
            Commitments = candidates.Incumbents, Proposed = new() { Add = [helper] } });
        Assert.DoesNotContain(forecast.Proposed.Coupled!.Alternatives, a => a.Feasible == Known(true));
        Assert.Contains(forecast.Proposed.Coupled.Alternatives.SelectMany(a => a.Findings),
            f => f.Reason == "conflicting_selected_target_owners" && f.Commitment == "horseshoes");
    }
    /// <summary>The retained pawn plus <paramref name="helpers"/> proposed ones claim one target of the given capacity and stack.</summary>
    private static IReadOnlyList<ForecastFinding> ClaimsOnOneTarget(string kind, string target, Fact<int> maxPawns, int helpers,
        out bool anyFeasible, int count = 0, Fact<double> stock = default)
    {
        var (request, candidates) = Problem(1, 0, meal: false);
        var names = Enumerable.Range(1, helpers).Select(i => "helper" + i).ToArray();
        var roster = ReviewWorkResources(WorkResourceTestFacts.Create(["pawn", .. names]), Known<EquatableArray<ProductionBatch>>([]), new([]));
        var context = candidates.Context;
        ForecastNeedTask Claim(string task, string pawn) => new(task, pawn, kind, target, new(""), count, Known(.2), [])
            { MaxPawns = maxPawns, AvailableCount = stock };
        context = context with
        {
            Resources = roster.WorkView!,
            Needs = context.Needs with { Pawns = [context.Needs.Pawns[0], .. names.Select(n => context.Needs.Pawns[0] with { Pawn = n })] },
            NeedTasks = [Claim("ongoing-joy", "pawn"), .. names.Select(n => Claim(n + "-joy", n))],
            Activities = context.Activities with
            {
                Activities = [.. context.Activities.Activities, .. names.Select(n => new ForecastActivity(n + "-joy", "start", "start", []))],
                Options = [.. context.Activities.Options, .. names.Select(n => new ForecastActivityOption(n + "-joy", n, Known(20d), Known(0d)))],
                Pawns = [.. context.Activities.Pawns, .. names.Select(n => new ForecastPawnCapacity(n, "start", Known<EquatableArray<ForecastTimeWindow>>([new(0, 300)]), Known(1d)))]
            }
        };
        var forecast = ForecastColony(request with { Coupled = context, Commitments = candidates.Incumbents,
            Proposed = new() { Add = [.. names.Select(n => new ForecastCommitment(n + "-joy", []) { ActivityCompletions = [n + "-joy"] })] } });
        anyFeasible = forecast.Proposed.Coupled!.Alternatives.Any(a => a.Feasible == Known(true));
        return [.. forecast.Proposed.Coupled.Alternatives.SelectMany(a => a.Findings)];
    }

    [Fact]
    public void TwoPawnsCannotClaimAOneSlotBed()
    {
        var findings = ClaimsOnOneTarget("rest", "bed", Known(1), 1, out var feasible);
        Assert.False(feasible);
        Assert.Contains(findings, f => f.Reason == "conflicting_selected_target_owners" && f.Commitment == "bed");
    }

    [Fact]
    public void AnUnreadableCapacityBehavesAsOneSlot()
    {
        var findings = ClaimsOnOneTarget("rest", "bed", default, 1, out var feasible);
        Assert.False(feasible);
        Assert.Contains(findings, f => f.Reason == "conflicting_selected_target_owners" && f.Commitment == "bed");
    }

    [Fact]
    public void APinAdmitsUpToItsParticipantsAndRefusesOneMore()
    {
        var within = ClaimsOnOneTarget("joy", "pin", Known(3), 2, out var feasibleWithin);
        Assert.True(feasibleWithin);
        Assert.DoesNotContain(within, f => f.Reason is "conflicting_selected_target_owners" or "target_stack_quantity_exceeded");
        var over = ClaimsOnOneTarget("joy", "pin", Known(3), 3, out var feasibleOver);
        Assert.False(feasibleOver);
        Assert.Contains(over, f => f.Reason == "conflicting_selected_target_owners" && f.Commitment == "pin");
    }

    [Fact]
    public void ConsumersSharingAStackFitWithinItsQuantityAndAreRefusedBeyondIt()
    {
        // Three claimants of two units each against a stack of ten (capacity 10): six fits, then the stack is exhausted.
        var within = ClaimsOnOneTarget("joy", "stack", Known(10), 2, out var feasibleWithin, count: 2, stock: Known(6d));
        Assert.True(feasibleWithin);
        Assert.DoesNotContain(within, f => f.Reason == "target_stack_quantity_exceeded");
        var beyond = ClaimsOnOneTarget("joy", "stack", Known(10), 2, out var feasibleBeyond, count: 2, stock: Known(5d));
        Assert.False(feasibleBeyond);
        Assert.Contains(beyond, f => f.Reason == "target_stack_quantity_exceeded" && f.Commitment == "stack");
        Assert.DoesNotContain(beyond, f => f.Reason == "conflicting_selected_target_owners");
    }
    [Fact]
    public void NewlyUrgentFoodCanReleaseCurrentJoyThroughTheSameConsequenceComparison()
    {
        var (request, candidates) = Problem(.1, .001);
        var result = CompareActivityOutcomes(request, candidates);
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
        var result = CompareActivityOutcomes(request, candidates);
        Assert.Equal("current-joy", Assert.Single(result.Selection.Selected));
        Assert.Equal(result.BaselineVector, result.SelectedVector);
        var empty = Assert.Single(result.Rejected, p => p.Commitments.Count == 0);
        Assert.NotNull(empty.Consequences);
        Assert.True(empty.Consequences.Labor < result.BaselineVector.Labor);
        Assert.Throws<DomainException>(() => StartCoupledActivity(result, candidates, 1, "replacement", 100));
    }

    [Fact]
    public void ExhaustedOptionalSearchKeepsBaselineAndReportsItsBoundDeterministically()
    {
        var (request, candidates) = Problem(.1, .001);
        var first = CompareActivityOutcomes(request, candidates, 0);
        var second = CompareActivityOutcomes(request, candidates, 0);
        Assert.Equal("current-joy", Assert.Single(first.Selection.Selected));
        Assert.True(first.Selection.SearchTruncated);
        Assert.Contains("portfolio_search_truncated", first.Selection.Findings);
        Assert.Equal(first.Selection.Selected, second.Selection.Selected);
        Assert.Equal(first.Selection.Expansions, second.Selection.Expansions);
        Assert.Equal(first.SelectedVector, second.SelectedVector);
    }

    [Fact]
    public void IncumbentsAndNewAlternativesCannotReuseCandidateIdentity()
    {
        var (request, candidates) = Problem(.1, .001);
        candidates = candidates with { Alternatives = candidates.Incumbents };
        Assert.Throws<DomainException>(() => CompareActivityOutcomes(request, candidates));
    }
    [Fact]
    public void WaitingClassificationCannotBypassNeedServiceFeasibility()
    {
        var (request, candidates) = Problem(.1, .001);
        candidates = candidates with
        {
            Context = candidates.Context with { NonproductiveOccupancy = ["ongoing-joy"] }
        };
        Assert.Throws<DomainException>(() => CompareActivityOutcomes(request, candidates));
    }
}
