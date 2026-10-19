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
         new(PawnActivityKind.Recipe, "bench", "cooking", "", 0, 4567)
         { Recipe = new("bill", "recipe", [new("stack-a", 4), new("stack-b", 6)]) }])]);
    [Fact]
    public void HandsIntentAndJournalRetainExactTargetsAndDeadlines()
    {
        IntentActionRules.RegisterIntentKinds();
        var action = DomainAction.NewPawnActivitiesAction(new ActionId("action"), Batch());
        Assert.True(RimGovernor.Brain.Executor.Executor.PlainIntent(action.Kind));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var doc = JsonDocument.Parse(stream.ToArray());
        var decoded = Codecs.Default.Read<DomainAction>(doc.RootElement);
        var wire = IntentActionRules.IntentAction("action", decoded);
        Assert.Equal("meal", wire.Intent!.PawnActivities!.Batch!.Orders[0].Steps[1].Target);
        Assert.Equal(1234, wire.Intent.PawnActivities.Batch.Orders[0].Steps[0].Deadline);
        Assert.Equal("method", wire.Intent.PawnActivities.Batch.Orders[0].Method);
        Assert.Equal(Batch().Orders[0].Steps[2], wire.Intent.PawnActivities.Batch.Orders[0].Steps[2]);
        Assert.Equal(Batch().Orders[0].Steps[3], wire.Intent.PawnActivities.Batch.Orders[0].Steps[3]);
    }
    /// <summary>#3617: a task's prior steps (the roof-holder's RemoveRoof cell set before its Deconstruct) ride the task
    /// through the store codec and are validated like any step.</summary>
    [Fact]
    public void ATaskCarriesItsPriorStepsThroughTheCodecAndValidatesThem()
    {
        IntentActionRules.RegisterIntentKinds();
        var roof = new PawnActivityStep(PawnActivityKind.Work, "Thing_Wall1", "RemoveRoofs", "RemoveRoof", 2, 5678) { Cells = [new(1, 2), new(1, 3)] };
        var deconstruct = new PawnActivityStep(PawnActivityKind.Work, "Thing_Wall1", "Deconstruct", "Deconstruct", 0, 5678);
        var task = new SelectedActivityTask("task", "pawn", [], deconstruct, new(1, 2), 100) { PriorSteps = [roof] };
        var order = new PawnActivityOrder("pawn", "assignment", "method", 0, 1, false, [roof, deconstruct]) { Task = task.Id };
        var batch = new PawnActivities(1, [order]) { Tasks = [task] };
        batch.Validate();
        var action = DomainAction.NewPawnActivitiesAction(new ActionId("action"), batch);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var doc = JsonDocument.Parse(stream.ToArray());
        var decoded = Codecs.Default.Read<DomainAction>(doc.RootElement).PawnActivities!;
        Assert.Equal(task, Assert.Single(decoded.Tasks));
        Assert.Equal(roof, Assert.Single(decoded.Tasks[0].PriorSteps));

        void Refused(PawnActivityStep prior) => Assert.Throws<DomainException>(() => (batch with { Tasks = [task with { PriorSteps = [prior] }] }).Validate());
        Refused(roof with { Count = 1 });
        Refused(roof with { Deadline = 0 });
        Refused(roof with { Kind = PawnActivityKind.Deliver, Destination = new Cell(1, 1) });
    }
    /// <summary>#3613: a set step repeats one Work over its Targets (or Cells); Count is the set's size, the store codec
    /// keeps the set, and SameOrder tells orders apart by it.</summary>
    [Fact]
    public void ASetStepCarriesItsTargetsThroughTheCodecAndSameOrderTellsThemApart()
    {
        IntentActionRules.RegisterIntentKinds();
        var cut = new PawnActivityStep(PawnActivityKind.Work, "Thing_Plant_TreeOak1", "PlantsCut", "CutPlant", 3, 5678)
            { Targets = ["Thing_Plant_TreeOak1", "Thing_Plant_TreeOak2", "Thing_Plant_TreeOak3"] };
        var floor = new PawnActivityStep(PawnActivityKind.Work, "planned/room1/floor", "RemoveFloor", "RemoveFloor", 2, 5678) { Cells = [new(1, 2), new(3, 4)] };
        var order = new PawnActivityOrder("pawn", "assignment", "method", 0, 1, false, [cut, floor]);
        var batch = new PawnActivities(1, [order]);
        batch.Validate();
        var action = DomainAction.NewPawnActivitiesAction(new ActionId("action"), batch);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var doc = JsonDocument.Parse(stream.ToArray());
        var decoded = Codecs.Default.Read<DomainAction>(doc.RootElement).PawnActivities!;
        Assert.Equal(cut, decoded.Orders[0].Steps[0]);
        Assert.Equal(floor, decoded.Orders[0].Steps[1]);
        Assert.True(PawnActivities.SameOrder(order, decoded.Orders[0]));
        Assert.False(PawnActivities.SameOrder(order, order with { Steps = [cut with { Targets = ["Thing_Plant_TreeOak1", "Thing_Plant_TreeOak2", "Thing_Plant_TreeOak4"] }, floor] }));
        Assert.False(PawnActivities.SameOrder(order, order with { Steps = [cut, floor with { Cells = [new(1, 2), new(3, 5)] }] }));
        Assert.True(cut.IsSet);
        Assert.False((cut with { Targets = default }).IsSet);

        void Refused(PawnActivityStep step) => Assert.Throws<DomainException>(() => (batch with { Orders = [order with { Steps = [step] }] }).Validate());
        Refused(cut with { Count = 2 });
        Refused(cut with { Targets = ["Thing_Plant_TreeOak1", "Thing_Plant_TreeOak1", "Thing_Plant_TreeOak2"] });
        Refused(cut with { Kind = PawnActivityKind.Deliver, Destination = new Cell(1, 1) });
        Refused(cut with { Cells = [new(1, 1)], Count = 4 });
        Refused(floor with { Cells = [new(-1, 2), new(3, 4)] });
        Refused(cut with { Targets = ["", "Thing_Plant_TreeOak2", "Thing_Plant_TreeOak3"] });
    }
    [Fact]
    public void CookingRejectsMissingOrAmbiguousIngredientAuthorization()
    {
        var b = Batch();
        var cook = b.Orders[0].Steps[3];
        void Refused(PawnActivityStep step) => Assert.Throws<DomainException>(() =>
            (b with { Orders = [b.Orders[0] with { Steps = [step] }] }).Validate());
        Refused(cook with { Recipe = null });
        Refused(cook with { Recipe = cook.Recipe! with { Bill = "" } });
        Refused(cook with { Recipe = cook.Recipe! with { Recipe = "" } });
        Refused(cook with { Recipe = cook.Recipe! with { Ingredients = [] } });
        Refused(cook with { Recipe = cook.Recipe! with { Ingredients = [new("a", 1), new("a", 1)] } });
        Refused(cook with { Recipe = cook.Recipe! with { Ingredients = [new("a", 0)] } });
        Refused(cook with { Recipe = cook.Recipe! with { Ingredients = [new("", 1)] } });
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
