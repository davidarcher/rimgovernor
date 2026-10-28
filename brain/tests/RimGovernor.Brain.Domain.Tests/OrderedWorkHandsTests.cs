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

public sealed class OrderedWorkHandsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private sealed class Clock : IClock { public DateTimeOffset Now() => DateTimeOffset.UtcNow; }

    // Exercises real journal/Hands and typed transport. It makes no claim about native queue execution.
    private sealed class Native : IBoundary
    {
        public int Writes;
        public bool Stale;
        public OrderedWork? Written;
        public Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct) => Task.FromResult(new IntentInspection
        {
            StartedAt = DateTimeOffset.UtcNow, ObservedAt = DateTimeOffset.UtcNow,
            Current = Stale ? target.Snapshot with { Native = new(2) } : target.Snapshot, Tick = new(100),
        });
        public Task<IReadOnlyList<NativeReceipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct)
        {
            Writes++;
            var placement = Assert.Single(placements);
            Written = IntentActionRules.IntentAction(placement.Action.Id.Value, placement.Action).Intent!.GiveJob!.Queue;
            return Task.FromResult<IReadOnlyList<NativeReceipt>>([new()
            {
                Action = placement.Action.Id, Attempt = placement.Attempt, Snapshot = placement.Snapshot, Kind = Receipt.Accepted,
            }]);
        }
    }

    [Fact]
    public async Task AdmittedQueueUsesNormalHandsFreshnessAndDuplicateReceiptProtection()
    {
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-ordered-work-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
        journal.Review(new() { Current = World, Tick = new(100), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a") }, Ct);
        var native = new Native();
        using var hands = new Hands(new ExecutorJournal(journal), native, new Clock(), new Limits
        {
            MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10),
        }, new PlanAuthorizer(journal, true));
        hands.UpdateAuthority(new() { Snapshot = World, Enabled = true });
        var action = DomainAction.NewOrderedWorkAction(new("queue-0"), OrderedWorkTests.Request());
        var plan = new PlanSpec(new("queue"), new(1), [action]);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new("intervention"), "", plan, Ct).Outcome);
        var competing = new PlanSpec(new("competing"), new(1),
            [DomainAction.NewOrderedWorkAction(new("competing-0"), OrderedWorkTests.Request() with { Jobs = [new("Deconstruct", "other-wall")] })]);
        Assert.NotEqual(ClaimOutcome.Admitted,
            journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new("competing"), "", competing, Ct).Outcome);
        native.Stale = true;
        Assert.IsType<HeldException>(Assert.Single(await hands.RunBatchAsync(plan.Id, [action.Id], Ct)).Error);
        Assert.Equal(0, native.Writes);
        native.Stale = false;
        Assert.Null(Assert.Single(await hands.RunBatchAsync(plan.Id, [action.Id], Ct)).Error);
        Assert.Equal(OrderedWorkTests.Request(), native.Written);
        Assert.Equal(1, native.Writes);
        Assert.Equal(Stage.Completed, Assert.Single(journal.LoadPlan(plan.Id, Ct).Progress).View.Stage);
        // Completed here acknowledges dispatch, not physical completion of the intervention.
        Assert.Null(Assert.Single(await hands.RunBatchAsync(plan.Id, [action.Id], Ct)).Error);
        Assert.Equal(1, native.Writes);
    }
}
