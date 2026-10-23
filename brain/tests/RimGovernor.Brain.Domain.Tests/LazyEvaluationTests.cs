using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Lazy outcome evaluation (#3723) on the four-worker fixture: the production search prices an evaluated outcome's floors first and its
/// ledger only when a comparison reaches it, and returns exactly what the eager test control returns, having spent the same outcome
/// evaluations and expansions (#3722) while never pricing the ledgers of the outcomes a floor decided.
/// </summary>
public sealed class LazyEvaluationTests
{
    private static readonly Lazy<(ColonyForecastRequest Request, ForecastCoupledCandidates Generated)> FourWorkers = new(() =>
    {
        var facts = PortfolioAllocationTests.LargerFacts();
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 700)!.Priced(700);
        return (PortfolioAllocationTests.Request(facts, generated), generated);
    });

    private static readonly string[] Floors = [ForecastCoupledSelection.UrgentZeroStage, ForecastCoupledSelection.DeathFloorStage, ForecastCoupledSelection.BreakRiskStage];

    /// <summary>The lazy search returns the eager control's selection, vectors, findings, accounting and evidence: the same rows in the same
    /// order with the same reasons, and the same complete vector wherever the lazy search priced one.</summary>
    internal static void AssertPaired(ForecastCoupledSelection eager, ForecastCoupledSelection lazy)
    {
        Assert.Equal(eager.Selection.Selected, lazy.Selection.Selected);
        Assert.Equal(eager.Selection.Plan!.Allocations, lazy.Selection.Plan!.Allocations);
        Assert.Equal(eager.Selection.Findings, lazy.Selection.Findings);
        Assert.Equal(eager.SelectedVector, lazy.SelectedVector);
        Assert.Equal(eager.BaselineVector, lazy.BaselineVector);
        Assert.Equal(eager.Selected.Plan.Allocations, lazy.Selected.Plan.Allocations);
        Assert.Equal(eager.Baseline.Plan.Allocations, lazy.Baseline.Plan.Allocations);
        Assert.Equal(eager.Deferred, lazy.Deferred);
        Assert.Equal(eager.ServiceInvestments, lazy.ServiceInvestments);
        Assert.Equal((eager.OutcomeEvaluations, eager.BaselineEvaluations, eager.Selection.Expansions, eager.BaselineExpansions, eager.Selection.SearchTruncated),
            (lazy.OutcomeEvaluations, lazy.BaselineEvaluations, lazy.Selection.Expansions, lazy.BaselineExpansions, lazy.Selection.SearchTruncated));
        Assert.Equal(eager.SearchPhases, lazy.SearchPhases);
        Assert.Equal(eager.SelectedPhase, lazy.SelectedPhase);
        Assert.Equal(eager.Rejected.Count, lazy.Rejected.Count);
        foreach (var (first, second) in eager.Rejected.Zip(lazy.Rejected))
        {
            Assert.Equal(first.Commitments, second.Commitments);
            Assert.Equal(first.Reason, second.Reason);
            Assert.Equal(first.Consequences is not null, second.Assessed && ForecastPortfolioEvidence.BoundOf(second.Reason) is null);
            if (second.Consequences is not null) Assert.Equal(first.Consequences, second.Consequences);
        }
    }

    [Fact]
    public void TheLazySearchReturnsTheEagerSelectionEvidenceAndCountsOnTheFourWorkerFixture()
    {
        var (request, generated) = FourWorkers.Value;
        var lazy = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        var eager = ForecastCoupledSelection.CompareActivityOutcomesEagerly(request, generated);
        AssertPaired(eager, lazy);
        // A started evaluation costs one unit wherever its comparison stops (#3722): the counts match the eager search's and the phases'.
        Assert.Equal(lazy.OutcomeEvaluations, lazy.SearchPhases.Sum(p => p.OutcomeEvaluations));
        Assert.Contains(lazy.Rejected, r => r.Consequences is null && ForecastPortfolioEvidence.StageOf(r.Reason) is not null);
    }

    [Fact]
    public void AFloorDecidedLoserIsReportedUnevaluatedAndTheLedgerIsPricedOnlyWhereAComparisonReachedIt()
    {
        var (request, generated) = FourWorkers.Value;
        var lazy = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        var ranked = lazy.Rejected.Where(r => ForecastPortfolioEvidence.StageOf(r.Reason) is not null).ToArray();
        var atFloor = ranked.Where(r => Floors.Contains(ForecastPortfolioEvidence.StageOf(r.Reason))).ToArray();
        var beyond = ranked.Where(r => !Floors.Contains(ForecastPortfolioEvidence.StageOf(r.Reason))).ToArray();
        Assert.All(atFloor, r => Assert.Null(r.Consequences));
        Assert.All(beyond, r => Assert.NotNull(r.Consequences));
        // Both tiebreak rules appear: ties on every consequence are decided by the caller's incumbent and labor rules.
        Assert.Contains(beyond, r => ForecastPortfolioEvidence.StageOf(r.Reason) == ForecastCoupledSelection.LaborStage);
        Assert.Contains(beyond, r => ForecastPortfolioEvidence.StageOf(r.Reason) == ForecastCoupledSelection.LedgerStage);
        // The measured avoided work on the fixture at 700 ticks (docs/developers/testing/portfolio-allocations.md): of the ranked losers,
        // those whose ledger a floor made unnecessary against those a comparison priced. The evaluations are #3724's recorded 2,823 (#3722
        // recorded 3,138 before the bound skipped 20 subsets, 315 of them plan evaluations that would have lost at the urgent-zero floor).
        Assert.Equal((2823, 176, 2488), (lazy.OutcomeEvaluations, atFloor.Length, beyond.Length));
    }

    /// <summary>The urgent-zero unserved-pawn bound (#3724) on the four-worker fixture at the default budget: the pruned production search
    /// is the unpruned control's selection and evidence with the bound available throughout, and it skipped 20 extension subsets (those
    /// feeding neither starving pawn after the leader fed one) for 315 fewer plan evaluations and 1,088 fewer expansions
    /// (docs/developers/testing/portfolio-allocations.md).</summary>
    [Fact]
    public void ThePrunedSearchIsTheUnprunedSearchOnTheFourWorkerFixture()
    {
        var (request, generated) = FourWorkers.Value;
        var pruned = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        var unpruned = ForecastCoupledSelection.CompareActivityOutcomesUnpruned(request, generated);
        ForecastCoupledSelectionTests.AssertPrunedEquivalent(unpruned, pruned);
        Assert.Empty(pruned.BoundsUnavailable);
        Assert.Equal((3138, 12736), (unpruned.OutcomeEvaluations, unpruned.Selection.Expansions));
        Assert.Equal((2823, 11648, 20), (pruned.OutcomeEvaluations, pruned.Selection.Expansions, pruned.SearchPhases.Sum(p => p.Pruned)));
        Assert.Equal(20, pruned.SearchPhases.Single(p => p.Phase == "extend").Pruned);
    }
}
