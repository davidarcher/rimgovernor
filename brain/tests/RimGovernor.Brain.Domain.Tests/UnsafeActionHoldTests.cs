using System.Reflection;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Executor;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Hands = RimGovernor.Brain.Executor.Executor;
using Journal = RimGovernor.Brain.Store.Store;
using NativeReceipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class UnsafeActionHoldTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GenerationSnapshot Root = new(new("colony"), new(1), new("load"), new("plan"), new(1), new(1));

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now() => DateTimeOffset.UtcNow;
    }

    private sealed class Native : IBoundary
    {
        public int Writes;
        public Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct) => Task.FromResult(new IntentInspection
        {
            StartedAt = DateTimeOffset.UtcNow, ObservedAt = DateTimeOffset.UtcNow, Current = target.Snapshot, Tick = new(100)
        });
        public Task<IReadOnlyList<NativeReceipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct)
        {
            Writes++;
            throw new InvalidOperationException("vetoed actions must not reach native");
        }
    }

    public class VetoJournal : DispatchProxy
    {
        internal IJournal Inner = null!;
        internal readonly ActionVetoedException Veto = new("unsafe test action");
        internal readonly IOException HoldFailure = new("hold journal failed");
        internal bool FailHold;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IJournal.DispatchBatchAsync))
            {
                var attempts = (IReadOnlyList<BatchAttempt>)args![0]!;
                return Task.FromResult<IReadOnlyList<BatchResult>>(attempts.Select(_ => new BatchResult(null, Veto)).ToArray());
            }
            if (FailHold && targetMethod.Name == nameof(IJournal.HoldAsync))
                return Task.FromException<Progress>(HoldFailure);
            return targetMethod.Invoke(Inner, args);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VetoNeverWritesAndRetainsAnyHoldJournalFailure(bool failHold)
    {
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-unsafe-hold-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
        var action = DomainAction.NewBuildingAction(new("build"), new("TestWall", new(1, 1), Rotation.North, ""), ConstructionTier.Survive);
        journal.CreatePlan(new(Root.Plan, Root.Revision, [action]), Ct);
        var proxy = DispatchProxy.Create<IJournal, VetoJournal>();
        var faults = (VetoJournal)proxy;
        faults.Inner = new ExecutorJournal(journal);
        faults.FailHold = failHold;
        var native = new Native();
        using var hands = new Hands(proxy, native, new Clock(), new Limits
        {
            MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10)
        });
        hands.UpdateAuthority(new() { Snapshot = Root, Enabled = true });
        var result = Assert.Single(await hands.RunBatchAsync(Root.Plan, [action.Id], Ct));
        var errors = Assert.IsType<AggregateException>(result.Error).Flatten().InnerExceptions;
        Assert.Contains(errors, e => e is HeldException);
        Assert.Contains(faults.Veto, errors);
        Assert.Equal(failHold, errors.Contains(faults.HoldFailure));
        Assert.Equal(0, native.Writes);
    }
}
