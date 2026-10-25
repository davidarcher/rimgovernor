using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NeedTransitionMethodTests
{
    private static GenerationSnapshot Root => new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));

    /// <summary>
    /// #3593: a coupled transition names the full MethodId its need_methods row binds, not the progress row's label
    /// (which cut a coupled id's last GUID segment as a content hash); a Need that works no Method names none.
    /// </summary>
    [Fact]
    public void ACoupledTransitionNamesTheMethodItsNeedMethodsRowBinds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-transition-method-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        IntentActionRules.RegisterIntentKinds();
        _ = journal.Review(new ReviewRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var need = TestNeed.Id;
        var method = new MethodId("coupled-d67a4637-3335-4f0b-89b5-a609e587d024");
        var plan = new PlanSpec(new("coupled-plan"), new(1), [DomainAction.NewPawnActivitiesAction(new("coupled-plan-0"),
            new(1, [new("pawn", "assignment", method.Value, 0, 1, false, [new(PawnActivityKind.Hold, "", "", "", 0, 500)])]))]);
        TestNeed.Commit(journal, Root, method, plan, ct);
        var filings = new Dictionary<FilingKey, PlannerFiling>();
        PlannerReasons.FileReason(filings, need.FilingKey, Verdict.BuildingReasonExistingWork);
        var transitions = new PlannerReasonLog().Changed(filings, 100);

        var working = journal.RecordPlannerReasons(new Dictionary<FilingKey, PlannerNote>(),
            [new FamilyProgress(AdminFamily.FamilyId, true, [new NeedProgress(need, false, default, method)])], ct).Methods;
        var row = Assert.Single(PlannerReasons.TransitionRows(transitions, working, 100, DateTimeOffset.UnixEpoch));
        Assert.Equal(Assert.Single(journal.LoadNeed(need, Root, ct).Methods).Method.Value, row.Method);

        var none = journal.RecordPlannerReasons(new Dictionary<FilingKey, PlannerNote>(),
            [new FamilyProgress(AdminFamily.FamilyId, true, [new NeedProgress(need, false, new PlannerNote { Cause = Cause.NoWorker }, default)])], ct).Methods;
        Assert.Equal("", Assert.Single(PlannerReasons.TransitionRows(transitions, none, 100, DateTimeOffset.UnixEpoch)).Method);
    }
}
