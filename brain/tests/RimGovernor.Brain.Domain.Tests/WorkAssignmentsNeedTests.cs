using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The admin/work-assignments Need (#3304): it replaced the work-assignments Concern. Its predicate is the review's
/// coverage, cleanup, settings and coupled-activity measure; its Method commits under the Need owner, claims each pawn's
/// settings and each written policy, and is the only Need owner whose plan may carry work priorities.
/// </summary>
public sealed class WorkAssignmentsNeedTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Fact]
    public void TheNeedIsUnmetWhileWorkIsOwedMetWhenNotAndUnknownWhileUnread()
    {
        Assert.Equal(Satisfaction.Unmet, AdminFamily.WorkAssignmentsNeed(Known(true)).Satisfaction);
        Assert.Equal(Satisfaction.Met, AdminFamily.WorkAssignmentsNeed(Known(false)).Satisfaction);
        var unread = Assert.Single(new AdminFamily(new AdminNeeds { WorkAssignments = true }).Derive(new AdminFacts()));
        Assert.Equal(AdminFamily.WorkAssignments, unread.Id);
        Assert.Equal(Satisfaction.Unknown, unread.Satisfaction);
        Assert.Empty(new AdminFamily(new AdminNeeds()).Derive(new AdminFacts { WorkAssignments = Known(true) }));
    }

    [Fact]
    public void WorkIsOwedForUncoveredWorkIdlePawnsOwedSettingsOrCoupledWorkAndUnknownWhileCoverageIsUnread()
    {
        var covered = WorkResourceTestFacts.Create("pawn") with { WorkCoverage = Known(true), CleanupPawns = Known(false) };
        Assert.Equal(Known(false), RoundsRules.WorkAssignmentsOwed(covered, null));
        Assert.Equal(Known(true), RoundsRules.WorkAssignmentsOwed(covered with { WorkCoverage = Known(false) }, null));
        Assert.Equal(Known(true), RoundsRules.WorkAssignmentsOwed(covered with { CleanupPawns = Known(true) }, null));
        Assert.Equal(Known(true), RoundsRules.WorkAssignmentsOwed(covered with { HostilityOwed = Known(true) }, null));
        Assert.Equal(Known(true), RoundsRules.WorkAssignmentsOwed(covered with { NamesOwed = Known(true) }, null));
        Assert.Equal(Known(true), RoundsRules.WorkAssignmentsOwed(covered with { CoupledWorkActive = true }, null));
        Assert.False(RoundsRules.WorkAssignmentsOwed(covered with { WorkCoverage = default }, null).IsKnown);
        // An owed setting is owed whatever the coverage read says.
        Assert.Equal(Known(true), RoundsRules.WorkAssignmentsOwed(covered with { WorkCoverage = default, MedicalCareOwed = Known(true) }, null));
    }

    [Fact]
    public void AssignmentsCommitUnderTheNeedAloneAndAWrittenPolicyIsClaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-work-assignments-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
        static PlanSpec Plan(params Func<ActionId, DomainAction>[] actions)
        {
            var id = PlanIds.MintPlanId();
            return new PlanSpec(id, new PlanRevision(1), [.. actions.Select((a, i) => a(new ActionId(id.Value + "-" + i)))]);
        }
        Func<ActionId, DomainAction> Assign(string pawn) => a => DomainAction.NewWorkAssignmentAction(a, WorkAssignment.NewWorkAssignment(new(pawn), [new WorkSetting("TestWork", 1)]));
        Func<ActionId, DomainAction> Reading(string name) => a => DomainAction.NewReadingPolicyAction(a, new ReadingPolicy(name, ["TestBook"]));

        var work = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-a"), "", Plan(Assign("a"), Reading("a")), ct);
        Assert.Equal(ClaimOutcome.Admitted, work.Outcome);
        Assert.Equal(AdminFamily.WorkAssignments.FilingKey, journal.PlanMethod(work.Owner.Methods[0].Plan, ct)!.Concern);
        Assert.Equal(ClaimOutcome.Continued, journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-a"), "", Plan(Assign("a")), ct).Outcome);

        // Another Method writing the same reading policy waits on its claim; another pawn's priorities are admitted.
        var held = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-reading"), "", Plan(Reading("a")), ct);
        Assert.Equal(ClaimOutcome.Conflict, held.Outcome);
        Assert.Equal("policy/reading/a", held.Conflict!.Claim);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-b"), "", Plan(Assign("b")), ct).Outcome);

        // No other Need sets work priorities (an unclaimed pawn, so admission rather than a claim refuses it).
        Assert.ThrowsAny<Exception>(() => journal.CommitClaimedMethod(AdminFamily.Burial, World, new MethodId("work-c"), "", Plan(Assign("c")), ct));
        Assert.Empty(journal.LoadNeed(AdminFamily.Burial, World, ct).Methods);
    }
}
