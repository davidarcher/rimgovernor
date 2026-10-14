using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
namespace RimGovernor.Brain.Domain.Tests;
public sealed class PawnActivitiesTests
{
    private static PawnActivities Batch() => new(1, [new("pawn", "assignment", "method", 0, 1, false,
        [new(PawnActivityKind.Hold, "", "", "", 0, 1234, "filth"), new(PawnActivityKind.Eat, "meal", "", "", 1, 2345),
         new(PawnActivityKind.Deliver, "harvest", "", "", 3, 3456) { Destination = new Cell(12, 34) },
         new(PawnActivityKind.Cook, "bench", "cooking", "", 0, 4567)
         { Cooking = new("bill", "recipe", [new("stack-a", 4), new("stack-b", 6)]) }])]);
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
        Assert.Equal(Batch().Orders[0].Steps[2], wire.Intent.PawnActivities.Batch.Orders[0].Steps[2]);
        Assert.Equal(Batch().Orders[0].Steps[3], wire.Intent.PawnActivities.Batch.Orders[0].Steps[3]);
    }
    [Fact]
    public void CookingRejectsMissingOrAmbiguousIngredientAuthorization()
    {
        var b = Batch();
        var cook = b.Orders[0].Steps[3];
        void Refused(PawnActivityStep step) => Assert.Throws<DomainException>(() =>
            (b with { Orders = [b.Orders[0] with { Steps = [step] }] }).Validate());
        Refused(cook with { Cooking = null });
        Refused(cook with { Cooking = cook.Cooking! with { Bill = "" } });
        Refused(cook with { Cooking = cook.Cooking! with { Recipe = "" } });
        Refused(cook with { Cooking = cook.Cooking! with { Ingredients = [] } });
        Refused(cook with { Cooking = cook.Cooking! with { Ingredients = [new("a", 1), new("a", 1)] } });
        Refused(cook with { Cooking = cook.Cooking! with { Ingredients = [new("a", 0)] } });
        Refused(cook with { Cooking = cook.Cooking! with { Ingredients = [new("", 1)] } });
        Refused(cook with { WorkGiver = "" });
        Refused(cook with { Job = "choose-bill" });
        Refused(cook with { Count = 2 });
        Refused(cook with { Kind = PawnActivityKind.Hold });
    }
    [Fact]
    public void DeliveryRequiresAnExactCellAndPositiveCountWithoutWorkSelection()
    {
        var b = Batch();
        var delivery = b.Orders[0].Steps[2];
        void Refused(PawnActivityStep step) => Assert.Throws<DomainException>(() =>
            (b with { Orders = [b.Orders[0] with { Steps = [step] }] }).Validate());
        Refused(delivery with { Destination = null });
        Refused(delivery with { Destination = new Cell(-1, 0) });
        Refused(delivery with { Count = 0 });
        Refused(delivery with { Target = "" });
        Refused(delivery with { WorkGiver = "choose-storage" });
        Refused(delivery with { Kind = PawnActivityKind.Hold });
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
