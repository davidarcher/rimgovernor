using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class SelfTendPermissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssignmentReviewCannotReplaceObservedSelfTreatmentPermission(bool proposed)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-self-tend-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        GenerationSnapshot world = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        var facts = WorkResourceTestFacts.Create("patient");
        facts = facts with { WorkCoverage = Fact.Known(false), ForecastObserved = facts.ForecastObserved with
        { WorkPawns = Fact.Known<EquatableArray<WorkPawn>>([PawnFixtures.Worker("patient")]) } };
        _ = journal.ReviewRounds(new() { Current = world, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        var plan = new PlanSpec(new("obsolete-setting"), new(1),
            [DomainAction.NewPawnSettingsAction(new("setting"), PawnSettings.NewSelfTendSetting(new("patient"), proposed))]);
        // The admin/work-assignments Need's Method is refused: self-treatment is an observed permission (#3304).
        var commit = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, world, new("obsolete-self-tend-selector"), "", plan, ct);
        Assert.Equal(ClaimOutcome.Refused, commit.Outcome);
        Assert.Contains("unsupported_assignment_self_tend_policy", commit.Refusal!.Message);
        Assert.Empty(journal.LoadNeed(AdminFamily.WorkAssignments, world, ct).Methods);
    }
}
