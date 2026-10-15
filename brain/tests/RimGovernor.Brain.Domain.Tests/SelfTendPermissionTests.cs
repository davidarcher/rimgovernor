using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;
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
        { WorkPawns = Fact.Known<EquatableArray<WorkPawn>>([new() { Id = new("patient"), SelfTend = Fact.Known(true), Available = Fact.Known(true) }]) } };
        var review = journal.ReviewRounds(new() { Current = world, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == EnsureWorkAssignments);
        var plan = new PlanSpec(new("obsolete-setting"), new(1),
            [DomainAction.NewPawnSettingsAction(new("setting"), PawnSettings.NewSelfTendSetting(new("patient"), proposed))]);
        var error = Assert.Throws<NotAdmittedException>(() => journal.CommitMethod(owner.Standard.Id,
            owner.Revision, new("obsolete-self-tend-selector"), plan, ct));
        Assert.Contains("unsupported_assignment_self_tend_policy", error.Message);
        Assert.Empty(journal.LoadOwnerMethods(owner, ct));
        Assert.Equal(owner.Revision, journal.LoadStandard(owner.Standard.Id, ct).Revision);
    }
}
