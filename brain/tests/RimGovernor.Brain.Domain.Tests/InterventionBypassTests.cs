using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Binding;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 5.6: the forecast reaches Hands only through <see cref="ForecastCoupledSelection.StartCoupledActivity"/> (called once, by
/// <c>ColonyReview.AdmitCoupledSelection</c>, before <c>CommitClaimedMethod(AdminFamily.WorkAssignments, …)</c> and
/// <c>DomainAction.NewPawnActivitiesAction</c>). Each way a selection could bypass the premise gate there is refused or stays
/// routine native work: a conditional-scenario basis, an empty schedule, a winning plan with no premise, a justified verdict with
/// no purpose of its own, a carried incumbent, production bill work and external trade work.
/// </summary>
public sealed class InterventionBypassTests
{
    private static ForecastActivityPlan SupplyPlan(ForecastActivity supply) =>
        new([new(supply.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);

    /// <summary>The kitchen's justified ingredient delivery (InterventionHandsTests (h)): the one selection that does reach Hands.</summary>
    private static (ForecastCoupledSelection Selection, ForecastCoupledCandidates Candidates, ForecastActivity Supply) Justified()
    {
        var (compared, candidates, supply) = InterventionHandsTests.StalledKitchen(InterventionHandsTests.MealStall(UnmetReason.IngredientsPending));
        var plan = SupplyPlan(supply);
        var justified = Assert.IsType<InterventionJustification.Justified>(InterventionRules.Gate([supply.Id], plan, compared.BaselineVector,
            compared.BaselineVector, candidates.Context, InterventionHandsTests.Kitchen));
        var selection = compared with { Selection = compared.Selection with { Selected = [supply.Id], Plan = plan }, Justification = justified };
        Assert.NotNull(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "bypass-control", 100));
        return (selection, candidates, supply);
    }

    [Fact]
    public void AConditionalScenarioBasisNeverStartsAnOrder()
    {
        var (selection, candidates, _) = Justified();
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection with { Basis = ForecastComparisonBasis.ConditionalScenario },
            candidates, 1, "scenario", 100));
    }

    [Fact]
    public void AnEmptyScheduleStartsNoOrder()
    {
        var (selection, candidates, _) = Justified();
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection with { Selection = selection.Selection with { Selected = [] } },
            candidates, 1, "empty", 100));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection with { Selection = selection.Selection with { Plan = null } },
            candidates, 1, "unplanned", 100));
    }

    /// <summary>A plan that wins the ranking but whose stall no exact order answers has no premise: its work stays withheld native
    /// work, so ranking alone dispatches nothing, and without a justified verdict nothing is re-admitted.</summary>
    [Theory]
    [InlineData(UnmetReason.BenchesExhausted)]
    [InlineData(UnmetReason.WorkUnstarted)]
    public void AWinningPlanWithoutAPremiseStaysNative(UnmetReason reason)
    {
        var (compared, candidates, supply) = InterventionHandsTests.StalledKitchen(InterventionHandsTests.MealStall(reason));
        var plan = SupplyPlan(supply);
        var refused = Assert.IsType<InterventionJustification.Refused>(InterventionRules.Justify(plan, plan.Allocations,
            compared.BaselineVector, compared.BaselineVector, candidates.Context, InterventionHandsTests.Kitchen));
        var ranked = compared.Selection with { Selected = [supply.Id], Plan = plan };
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(compared with { Selection = ranked, Justification = refused }, candidates, 1, "refused", 100));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(compared with { Selection = ranked, Justification = null }, candidates, 1, "unjudged", 100));
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(plan, candidates.Context).Allocations);
    }

    [Fact]
    public void AJustifiedVerdictWithoutThePawnsOwnPurposeIssuesNothing()
    {
        var (selection, candidates, _) = Justified();
        var justified = (InterventionJustification.Justified)selection.Justification!;
        Assert.Throws<DomainException>(() => ForecastCoupledSelection.StartCoupledActivity(
            selection with { Justification = justified with { Interventions = [] } }, candidates, 1, "purposeless", 100));
    }

    [Fact]
    public void ACarriedIncumbentIsForecastOnly()
    {
        var (selection, candidates, _) = Justified();
        var carried = candidates with { Incumbents = [new ForecastCommitment("incumbent", [])] };
        Assert.Throws<DomainException>(() => ForecastCoupledSelection.StartCoupledActivity(
            selection with { Selection = selection.Selection with { Selected = [.. selection.Selection.Selected, "incumbent"] } }, carried, 1, "incumbent", 100));
    }

    /// <summary>Production bill work is native: the gate never re-admits the bill, and even a forged re-admission of it yields a
    /// Recipe step validation refuses before it can become a Hands action.</summary>
    [Fact]
    public void ProductionBillWorkNeverReachesHands()
    {
        var (selection, candidates, supply) = Justified();
        var cook = candidates.Context.Bindings.Single(b => b.Value.Kind == PawnActivityKind.Recipe && b.Key.Pawn == "pawn"
            && candidates.Context.Activities.Activities.Single(t => t.Id == b.Key.Task).Prerequisites.Contains(supply.Id)).Key.Task;
        var plan = new ForecastActivityPlan([new(cook, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);
        Assert.Empty(InterventionRules.Readmitted(plan, selection.BaselineVector, selection.BaselineVector, candidates.Context, InterventionHandsTests.Kitchen));
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(plan, candidates.Context).Allocations);
        var justified = (InterventionJustification.Justified)selection.Justification!;
        var forged = selection with { Selection = selection.Selection with { Selected = [cook], Plan = plan },
            Justification = justified with { Readmitted = [cook] } };
        var batch = ForecastCoupledSelection.StartCoupledActivity(forged, candidates, 1, "forged-bill", 100);
        Assert.Throws<DomainException>(() => DomainAction.NewPawnActivitiesAction(new("forged-bill-0"), Assert.IsType<PawnActivities>(batch)));
    }

    /// <summary>External trade work has its own executor: binding drops it, a forged re-admission included.</summary>
    [Fact]
    public void ExternalTradeWorkIsNeverAnExactOrder()
    {
        var request = Assert.Single(ForecastTradeRequests.Options(ForecastTradeRequestTests.Catalog(new("Arm")), new RimGovernor.Brain.GameFacts.Observations.TradeVisitFacts
        { Requests = [ForecastTradeRequestTests.Request() with { ConsoleId = "console", ConsoleCell = new(1, 1) }] }, 800));
        var stock = new StockArrival { Requests = [request] };
        var work = Assert.Single(ForecastTradeExecution.Work(stock));
        var context = ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        { Stock = stock, TradeWork = [work], Activities = new() { Deadline = 100, Activities = [] } };
        var plan = new ForecastActivityPlan([new(work.Task, work.Pawn, 0, 0, 0, 10, 0, 10, 10, 0)], 10, 0);
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(plan, context).Allocations);
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(plan, context, new HashSet<string>(StringComparer.Ordinal) { work.Task }).Allocations);
    }
}
