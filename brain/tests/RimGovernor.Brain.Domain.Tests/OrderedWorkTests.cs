using System.Text.Json;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class OrderedWorkTests
{
    internal static OrderedWork Request() => new(new("a"), 1, "remove obstructing walls",
        [new("Deconstruct", "wall-a"), new("Deconstruct", "wall-b")]);
    private static OrderedWorkObservation Installed() => new(Request(), [new(101, OrderedWorkStatus.Running), new(102, OrderedWorkStatus.Queued)]);

    [Fact]
    public void DuplicateCannotReissueAfterPartialCompletionCancellationOrReload()
    {
        var request = Request();
        Assert.Equal(OrderedWorkOperation.Install, OrderedWorkProtocol.Operation(request, null));
        var partial = OrderedWorkProtocol.Ended(Installed(), 101, true);
        partial = OrderedWorkProtocol.Observe(partial, 102, []);
        var loaded = FactsJson.Deserialize<OrderedWorkObservation>(FactsJson.Serialize(partial));
        Assert.Equal(partial, loaded);
        Assert.Equal(OrderedWorkOperation.Read, OrderedWorkProtocol.Operation(request, loaded));
        Assert.Equal(OrderedWorkStatus.Succeeded, loaded.Jobs[0].Status);
        Assert.Equal(OrderedWorkStatus.Running, loaded.Jobs[1].Status);
        Assert.Equal(OrderedWorkOperation.Refuse, OrderedWorkProtocol.Operation(request with { Sequence = 2 }, loaded));
        loaded = loaded with { Jobs = [loaded.Jobs[0], loaded.Jobs[1] with { Status = OrderedWorkStatus.Cancelled }] };
        Assert.Equal(OrderedWorkOperation.Read, OrderedWorkProtocol.Operation(request, loaded));
        Assert.Equal(OrderedWorkOperation.Cancel, OrderedWorkProtocol.Operation(request with { Cancel = true }, loaded));
        Assert.Equal(OrderedWorkOperation.Install, OrderedWorkProtocol.Operation(request with { Sequence = 2 }, loaded));
        Assert.Equal(OrderedWorkOperation.Refuse, OrderedWorkProtocol.Operation(request with { Sequence = 3 }, loaded));
    }

    [Fact]
    public void ReplacedAndMissingJobsNeverBecomeSuccessfulOrAdoptUnrelatedWork()
    {
        var observed = OrderedWorkProtocol.Observe(Installed(), 999, [102, 998]);
        Assert.Equal(OrderedWorkStatus.Gone, observed.Jobs[0].Status);
        Assert.Equal(OrderedWorkStatus.Queued, observed.Jobs[1].Status);
        observed = OrderedWorkProtocol.Ended(observed, 999, true);
        Assert.Equal(OrderedWorkStatus.Gone, observed.Jobs[0].Status);
        observed = OrderedWorkProtocol.Observe(observed, null, [998]);
        Assert.All(observed.Jobs, j => Assert.Equal(OrderedWorkStatus.Gone, j.Status));
        Assert.Equal([101, 102], observed.Jobs.Select(j => j.JobId));
        Assert.Equal(OrderedWorkOperation.Read, OrderedWorkProtocol.Operation(Request(), observed));
    }

    [Fact]
    public void SameSequenceCannotChangePurposeTargetsOrOrder()
    {
        var installed = Installed();
        Assert.Equal(OrderedWorkOperation.Refuse, OrderedWorkProtocol.Operation(Request() with { Purpose = "other reason" }, installed));
        Assert.Equal(OrderedWorkOperation.Refuse, OrderedWorkProtocol.Operation(Request() with { Jobs = [Request().Jobs[1], Request().Jobs[0]] }, installed));
        Assert.Equal(OrderedWorkOperation.Refuse, OrderedWorkProtocol.Operation(Request() with { Cancel = true, Sequence = 2 }, installed));
        Assert.Equal(OrderedWorkOperation.Refuse, OrderedWorkProtocol.Operation(Request() with { Cancel = true }, null));
    }

    [Fact]
    public void QueueSurvivesJournalAndTypedOrderWithoutChangingOrderOrIdentity()
    {
        var action = DomainAction.NewOrderedWorkAction(new("queue"), Request());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var doc = JsonDocument.Parse(stream.ToArray());
        var restored = Codecs.Default.Read<DomainAction>(doc.RootElement);
        Assert.Equal(action, restored);
        IntentActionRules.RegisterIntentKinds();
        var order = IntentActionRules.IntentAction(action.Id.Value, restored);
        var roundtrip = OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.Action>(OrdersJson.Serialize(order));
        Assert.Equal(Request(), roundtrip.Intent!.GiveJob!.Queue);
        var receipt = new RimGovernor.Brain.GameFacts.Receipts.JobEffect { PawnId = "a", Issued = true, Queue = Installed() };
        Assert.Equal(receipt, FactsJson.Deserialize<RimGovernor.Brain.GameFacts.Receipts.JobEffect>(FactsJson.Serialize(receipt)));
    }

    [Fact]
    public void PendingDispatchClaimsPawnAndEveryTargetButCancellationDoesNotClaimNewWork()
    {
        var action = DomainAction.NewOrderedWorkAction(new("queue"), Request());
        var evidence = ProductionRules.WorkAdmissionEvidenceOf(WorkResourceTestFacts.Create("a"), new ProductionBatchCensus.Read([]));
        var claims = WorkRules.WorkClaimsOf(evidence,
            [new(new("method"), action, new ProgressView { Action = action.Id, Stage = Stage.Pending })]);
        Assert.Equal(["a"], claims.Pawns);
        Assert.Contains("wall-a", claims.Targets);
        Assert.Contains("wall-b", claims.Targets);
        var cancel = DomainAction.NewOrderedWorkAction(new("cancel"), Request() with { Cancel = true });
        var cancelled = WorkRules.WorkClaimsOf(evidence,
            [new(new("method"), cancel, new ProgressView { Action = cancel.Id, Stage = Stage.Pending })]);
        Assert.Empty(cancelled.Pawns);
        Assert.Empty(cancelled.Targets);
    }
}
