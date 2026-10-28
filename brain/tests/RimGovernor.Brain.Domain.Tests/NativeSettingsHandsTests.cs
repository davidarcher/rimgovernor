using RimGovernor.Brain.Executor;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Hands = RimGovernor.Brain.Executor.Executor;
using Journal = RimGovernor.Brain.Store.Store;
using NativeReceipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeSettingsHandsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private sealed class Clock : IClock { public DateTimeOffset Now() => DateTimeOffset.UtcNow; }

    // Fault injection at the real Hands boundary; this does not simulate vanilla obeying a timetable.
    private sealed class Native : IBoundary
    {
        public bool Stale, Refuse;
        public int Reads, Writes;
        public IReadOnlyList<string> Applied = [];
        public Dictionary<string, int> AppliedWork = new();
        public Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(new IntentInspection { StartedAt = DateTimeOffset.UtcNow, ObservedAt = DateTimeOffset.UtcNow,
                Current = Stale ? target.Snapshot with { Native = new(2) } : target.Snapshot, Tick = new(100 + Reads) });
        }
        public Task<IReadOnlyList<NativeReceipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct)
        {
            Writes++;
            var placement = Assert.Single(placements);
            var intent = IntentActionRules.IntentAction(placement.Action.Id.Value, placement.Action).Intent!.WorkSettings!;
            Assert.Equal("a", intent.PawnId);
            if (!Refuse)
            {
                Applied = intent.Schedule?.AssignmentDefs ?? [];
                AppliedWork = intent.Work.ToDictionary(w => w.WorkTypeDef!, w => w.Priority, StringComparer.Ordinal);
            }
            return Task.FromResult<IReadOnlyList<NativeReceipt>>([new() { Action = placement.Action.Id, Attempt = placement.Attempt,
                Snapshot = placement.Snapshot, Kind = Refuse ? Receipt.Refused : Receipt.Accepted,
                Refusal = Refuse ? new("precondition", "pawn became drafted", RefusalClass.Transient) : null }]);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly Journal Journal;
        public readonly Native Native = new();
        public readonly Hands Hands;
        public Rig()
        {
            IntentActionRules.RegisterIntentKinds();
            Journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-native-settings-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
            Journal.Review(new() { Current = World, Tick = new(100), Enabled = true,
                Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a") }, Ct);
            Hands = new(new ExecutorJournal(Journal), Native, new Clock(), new Limits
                { MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10) }, new PlanAuthorizer(Journal, true));
            Hands.UpdateAuthority(new() { Snapshot = World, Enabled = true });
        }
        public PlanSpec Admit(string id, RimGovernor.Brain.GameFacts.Observations.NeedsForecastPawn needs)
        {
            var settings = Assert.Single(WorkRules.NativeSettingsChanges([PawnFixtures.Worker("a")], [new(new("a"), [])], NeedsFixtures.Read(100, needs)));
            return Admit(id, settings);
        }
        public PlanSpec Admit(string id, WorkAssignment settings)
        {
            var plan = new PlanSpec(new(id), new(1), [DomainAction.NewWorkAssignmentAction(new(id + "-0"), settings)]);
            Assert.Equal(ClaimOutcome.Admitted, Journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new("work-settings"), "", plan, Ct).Outcome);
            return plan;
        }
        public Task<IReadOnlyList<BatchItem>> Run(PlanSpec plan) => Hands.RunBatchAsync(plan.Id, [plan.Actions[0].Id], Ct);
        public Stage Stage(PlanSpec plan) => Assert.Single(Journal.LoadPlan(plan.Id, Ct).Progress).View.Stage;
        public void Dispose() { Hands.Dispose(); Journal.Dispose(); }
    }

    [Fact]
    public async Task SupportedFoodRecoveryRiskChangesNativePrioritiesThroughAdmissionAndHands()
    {
        using var rig = new Rig();
        var facts = NativeFoodAssessmentTests.Facts();
        var worker = NativeFoodAssessmentTests.Worker();
        var demand = WorkRules.WorkDemand(facts, true);
        Assert.True(demand.Food.NeedsResponse);
        var decision = WorkRules.PlanWork([worker], [], demand);
        var settings = Assert.Single(WorkRules.NativeSettingsChanges([worker], decision.Assignments, facts.ForecastObserved.Needs));
        var plan = rig.Admit("food-priorities", settings);
        Assert.Null(Assert.Single(await rig.Run(plan)).Error);
        Assert.Equal(Stage.Completed, rig.Stage(plan));
        Assert.Equal(1, rig.Native.AppliedWork[Wire.Name(WorkType.Cooking)]);
        Assert.Equal(2, rig.Native.AppliedWork[Wire.Name(WorkType.Construction)]);
        Assert.Null(plan.Actions[0].PawnActivities);
        // A completed settings action is not completed production or a cleared food risk.
        Assert.True(demand.Food.NeedsResponse);
        Assert.Equal(10, Assert.IsType<NativeFoodAssessment.Reviewed>(demand.Food).RunwayDays);
    }

    [Fact]
    public async Task StaleInspectionCannotWriteAndFreshRetryInspectsAgain()
    {
        using var rig = new Rig();
        var plan = rig.Admit("settings", NativeTimetableTests.Needs());
        rig.Native.Stale = true;
        Assert.IsType<HeldException>(Assert.Single(await rig.Run(plan)).Error);
        Assert.Equal(Stage.Pending, rig.Stage(plan));
        Assert.Equal(0, rig.Native.Writes);
        rig.Native.Stale = false;
        Assert.Null(Assert.Single(await rig.Run(plan)).Error);
        Assert.Equal(2, rig.Native.Reads);
        Assert.Equal(1, rig.Native.Writes);
        Assert.Equal(Stage.Completed, rig.Stage(plan));
        Assert.Equal(plan.Actions[0].WorkAssignment!.Schedule, rig.Native.Applied);
        Assert.Null(Assert.Single(await rig.Run(plan)).Error);
        Assert.Equal(1, rig.Native.Writes);
    }

    [Fact]
    public async Task RefusalReleasesSettingsClaimAndFreshReviewCanReplaceThePreference()
    {
        using var rig = new Rig();
        var plan = rig.Admit("old", NativeTimetableTests.Needs());
        rig.Native.Refuse = true;
        await rig.Run(plan);
        Assert.Equal(Stage.Unsuccessful, rig.Stage(plan));
        Assert.Empty(rig.Native.Applied);
        Assert.Equal(RefusalClass.Transient, Assert.Single(rig.Journal.LoadPlan(plan.Id, Ct).Progress).View.Refusal!.Class);
        rig.Native.Refuse = false;
        var needs = NativeTimetableTests.Needs();
        var fresh = rig.Admit("fresh", needs with { Targets = [NeedsFixtures.Service("bed", "rest", 0, .0005), needs.Targets[1]] });
        Assert.Null(Assert.Single(await rig.Run(fresh)).Error);
        Assert.Equal(2, rig.Native.Reads);
        Assert.Equal(Stage.Completed, rig.Stage(fresh));
        Assert.NotEqual(plan.Actions[0].WorkAssignment!.Schedule, rig.Native.Applied);
        Assert.Equal(fresh.Actions[0].WorkAssignment!.Schedule, rig.Native.Applied);
    }
}
