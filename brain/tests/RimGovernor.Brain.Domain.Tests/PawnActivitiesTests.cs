using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
namespace RimGovernor.Brain.Domain.Tests;
public sealed class PawnActivitiesTests
{
    private static PawnActivities Batch() => new(1, [new("pawn", "assignment", "method", 0, 1, false,
        [new(PawnActivityKind.Hold, "", "", "", 0, 1234, "filth"), new(PawnActivityKind.Eat, "meal", "", "", 1, 2345)])]);
    [Fact]
    public void HandsIntentAndJournalRetainExactTargetsAndDeadlines()
    {
        BridgeFunctions.RegisterIntentKinds();
        var action = DomainAction.NewPawnActivitiesAction(new ActionId("action"), Batch());
        Assert.True(RimGovernor.Brain.Executor.Executor.PlainIntent(action.Kind));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var doc = JsonDocument.Parse(stream.ToArray());
        var decoded = Codecs.Default.Read<DomainAction>(doc.RootElement);
        var wire = BridgeFunctions.IntentAction("action", decoded);
        Assert.Equal("meal", wire.Intent!.PawnActivities!.Batch!.Orders[0].Steps[1].Target);
        Assert.Equal(1234, wire.Intent.PawnActivities.Batch.Orders[0].Steps[0].Deadline);
        Assert.Equal("method", wire.Intent.PawnActivities.Batch.Orders[0].Method);
    }
    [Fact]
    public void DuplicatePawnsAndNonAdvancingRevisionsAreRejected()
    {
        var b = Batch();
        Assert.Throws<DomainException>(() => (b with { Orders = [b.Orders[0], b.Orders[0]] }).Validate());
        Assert.Throws<DomainException>(() => (b with { Orders = [b.Orders[0] with { ExpectedRevision = 1 }] }).Validate());
        Assert.Throws<DomainException>(() => (b with { AuthorityGeneration = 0 }).Validate());
    }
    [Fact]
    public void ReleaseCannotCarryWorkAndNeedsRequireExactTargets()
    {
        var b = Batch();
        Assert.Throws<DomainException>(() => (b with { Orders = [b.Orders[0] with { Release = true }] }).Validate());
        Assert.Throws<DomainException>(() => (b with { Orders = [b.Orders[0] with { Steps = [new(PawnActivityKind.Eat, "", "", "", 1, 100)] }] }).Validate());
    }
}