using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.GameAccess;
using Xunit;
namespace RimGovernor.Brain.Domain.Tests;
public sealed class PawnActivitiesTests
{
    [Fact]
    public void LegacyFlatJournalMapsNullCellsWithoutLosingExactBindings()
    {
        var source = new Cell(4, 5);
        var site = new Cell(20, 21);
        var work = new PawnActivityStep(PawnActivityKind.Work, "wall", "ConstructFinishFrames", "FinishFrame", 0, 500);
        var supply = new PawnActivityStep(PawnActivityKind.Supply, "steel", "", "", 3, 500) { Container = "wall" };
        var steps = Batch().Orders[0].Steps.Concat(new PawnActivityStep[]
        {
            work, supply,
            new PawnActivityStep(PawnActivityKind.Approach, "", "", "", 0, 500).At(source),
            new(PawnActivityKind.Sleep, "bed", "", "", 0, 500),
            new(PawnActivityKind.Recreation, "chess", "", "", 0, 500)
        }).ToArray();
        var batch = new PawnActivities(1, [new("pawn", "assignment", "method", 0, 1, false, steps)])
        {
            Tasks = [
                new("build", "pawn", [], work, site, 0),
                new("supply", "pawn", [], supply, site, 0) { Source = source },
                new("deliver", "pawn", [], steps[2], source, 0) { Source = source }
            ]
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, batch);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(stream.ToArray())!;
        void FlatStep(System.Text.Json.Nodes.JsonNode step)
        {
            if (!step.AsObject().ContainsKey("Destination")) step["Destination"] = null;
        }
        foreach (var step in legacy["Orders"]![0]!["Steps"]!.AsArray()) FlatStep(step!);
        foreach (var task in legacy["Tasks"]!.AsArray())
        {
            FlatStep(task!["Step"]!);
        }
        legacy["Tasks"]![0]!["Source"] = null;
        using var doc = JsonDocument.Parse(legacy.ToJsonString());
        var stored = Codecs.Default.Read<PawnActivities>(doc.RootElement);
        Assert.True(PawnActivities.SameOrder(batch.Orders[0], stored.Orders[0]));
        Assert.Equal(batch.Tasks.ToArray(), stored.Tasks.ToArray());
        Assert.Equal(site, stored.Tasks[0].Source);
        Assert.Equal(source, stored.Tasks[1].Source);
        Assert.Equal(source, stored.Tasks[2].Source);
        using var rewritten = new MemoryStream();
        using (var writer = new Utf8JsonWriter(rewritten)) Codecs.Default.Write(writer, stored);
        using var current = JsonDocument.Parse(rewritten.ToArray());
        Assert.False(current.RootElement.GetProperty("Orders")[0].GetProperty("Steps")[0].TryGetProperty("Destination", out _));
        Assert.Equal(20, current.RootElement.GetProperty("Tasks")[0].GetProperty("Source").GetProperty("X").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JournalRejectsMissingOrNullDestinationOnCellSteps(bool missing)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, Batch());
        var json = System.Text.Json.Nodes.JsonNode.Parse(stream.ToArray())!;
        var step = json["Orders"]![0]!["Steps"]![2]!.AsObject();
        if (missing) step.Remove("Destination"); else step["Destination"] = null;
        using var doc = JsonDocument.Parse(json.ToJsonString());
        Assert.ThrowsAny<Exception>(() => Codecs.Default.Read<PawnActivities>(doc.RootElement));
    }

    [Fact]
    public void CellStepsAndExactSourcesSurviveJournalAndNativeWire()
    {
        var source = new Cell(4, 5);
        var site = new Cell(20, 21);
        var delivery = new PawnActivityStep(PawnActivityKind.Deliver, "steel", "", "", 3, 500).At(site);
        var hold = new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, 500);
        var task = new SelectedActivityTask("delivery", "pawn", [], delivery, source, 0);
        var batch = new PawnActivities(1, [new("pawn", "assignment", "method", 0, 1, false, [delivery, hold])]) { Tasks = [task] };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, batch);
        using var doc = JsonDocument.Parse(stream.ToArray());
        var steps = doc.RootElement.GetProperty("Orders")[0].GetProperty("Steps");
        Assert.Equal(20, steps[0].GetProperty("Destination").GetProperty("X").GetInt32());
        Assert.False(steps[1].TryGetProperty("Destination", out _));
        Assert.Equal(4, doc.RootElement.GetProperty("Tasks")[0].GetProperty("Source").GetProperty("X").GetInt32());
        var stored = Codecs.Default.Read<PawnActivities>(doc.RootElement);
        Assert.Equal(source, Assert.Single(stored.Tasks).Source);
        Assert.Equal(site, Assert.IsType<CellActivityStep>(stored.Orders[0].Steps[0]).Destination);
        Assert.IsType<PawnActivityStep>(stored.Orders[0].Steps[1]);
        var request = new RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest(ConstructionFixtures.Context().Identity, stored);
        var wire = RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest>(
            RimGovernor.Brain.GameOrders.OrdersJson.Serialize(request));
        Assert.Equal(source, Assert.Single(wire.Batch.Tasks).Source);
        Assert.Equal(delivery, Assert.IsType<CellActivityStep>(wire.Batch.Orders[0].Steps[0]));
        Assert.IsType<PawnActivityStep>(wire.Batch.Orders[0].Steps[1]);
        Assert.False(PawnActivities.SameOrder(batch.Orders[0], batch.Orders[0] with { Steps = [delivery.At(new(22, 23)), hold] }));
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(stream.ToArray())!;
        malformed["Orders"]![0]!["Steps"]![1]!["Destination"] = new System.Text.Json.Nodes.JsonObject { ["X"] = source.X, ["Z"] = source.Z };
        using var invalid = JsonDocument.Parse(malformed.ToJsonString());
        Assert.Throws<JsonException>(() => Codecs.Default.Read<PawnActivities>(invalid.RootElement));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeWireRejectsMissingExactActivityCells(bool removeSource)
    {
        var step = new PawnActivityStep(PawnActivityKind.Deliver, "steel", "", "", 3, 500).At(new(20, 21));
        var task = new SelectedActivityTask("delivery", "pawn", [], step, new(4, 5), 0);
        var batch = new PawnActivities(1, [new("pawn", "assignment", "method", 0, 1, false, [step])]) { Tasks = [task] };
        var request = new RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest(ConstructionFixtures.Context().Identity, batch);
        var json = System.Text.Json.Nodes.JsonNode.Parse(RimGovernor.Brain.GameOrders.OrdersJson.Serialize(request))!;
        var target = removeSource ? json["batch"]!["tasks"]![0]! : json["batch"]!["orders"]![0]!["steps"]![0]!;
        Assert.True(target.AsObject().Remove(removeSource ? "source" : "destination"));
        Assert.Throws<JsonException>(() => RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest>(json.ToJsonString()));
    }

    private static PawnActivities Batch() => new(1, [new("pawn", "assignment", "method", 0, 1, false,
        [new(PawnActivityKind.Hold, "", "", "", 0, 1234), new(PawnActivityKind.Eat, "meal", "", "", 1, 2345),
         new PawnActivityStep(PawnActivityKind.Deliver, "harvest", "", "", 3, 3456).At(new Cell(12, 34)),
         new(PawnActivityKind.Work, "plant", "GrowerHarvest", "Harvest", 0, 4567)])]);
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
    /// <summary>A primitive multi-step order retains its sequence through the store codec.</summary>
    [Fact]
    public void AMultiStepOrderRetainsItsSequenceThroughTheCodec()
    {
        IntentActionRules.RegisterIntentKinds();
        var supply = new PawnActivityStep(PawnActivityKind.Supply, "Thing_Steel1", "", "", 2, 5678) { Container = "Thing_Frame1" };
        var finish = new PawnActivityStep(PawnActivityKind.Work, "Thing_Frame1", "ConstructFinishFrames", "FinishFrame", 0, 5678);
        var task = new SelectedActivityTask("task", "pawn", [], finish, new(1, 2), 100);
        var order = new PawnActivityOrder("pawn", "assignment", "method", 0, 1, false, [supply, finish]) { Task = task.Id };
        var batch = new PawnActivities(1, [order]) { Tasks = [task] };
        batch.Validate();
        var action = DomainAction.NewPawnActivitiesAction(new ActionId("action"), batch);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var doc = JsonDocument.Parse(stream.ToArray());
        var decoded = Codecs.Default.Read<DomainAction>(doc.RootElement).PawnActivities!;
        Assert.Equal(task, Assert.Single(decoded.Tasks));
        Assert.Equal([supply, finish], Assert.Single(decoded.Orders).Steps);
    }
    /// <summary>
    /// Task 5.6: exact Work is only practice, a food harvest, a stalled frame's finish or an unserved tend. Routine jobs
    /// (bills, surgery, research, sowing, roofing, removal, repair, firefighting) are native work: Validate refuses them on an
    /// order step and on a task step, and a Recipe-kind step is never an exact order.
    /// </summary>
    [Theory]
    [InlineData("DoBill")]
    [InlineData("Research")]
    [InlineData("Sow")]
    [InlineData("BuildRoof")]
    [InlineData("RemoveRoof")]
    [InlineData("Deconstruct")]
    [InlineData("Uninstall")]
    [InlineData("CutPlant")]
    [InlineData("Mine")]
    [InlineData("RemoveFloor")]
    [InlineData("Repair")]
    [InlineData("BeatFire")]
    public void RoutineWorkIsNeverAnExactOrder(string job)
    {
        var b = Batch();
        var harvest = b.Orders[0].Steps[3];
        b.Validate();
        var routine = harvest with { Job = job, WorkGiver = "routine" };
        Assert.False(PawnActivities.ExactWork(job));
        Assert.Throws<DomainException>(() => (b with { Orders = [b.Orders[0] with { Steps = [routine] }] }).Validate());
        Assert.Throws<DomainException>(() => (b with { Tasks = [new("routine", "pawn", [], routine, new(1, 1), 0)] }).Validate());
        Assert.Throws<DomainException>(() => (b with { Orders = [b.Orders[0] with { Steps = [harvest with { Kind = PawnActivityKind.Recipe, Job = "" }] }] }).Validate());
        foreach (var exact in new[] { "Harvest", "FinishFrame", "TendPatient" })
        {
            Assert.True(PawnActivities.ExactWork(exact));
            (b with { Orders = [b.Orders[0] with { Steps = [harvest with { Job = exact }] }] }).Validate();
        }
    }
    [Fact]
    public void DeliveryRequiresAnExactCellAndPositiveCountWithoutWorkSelection()
    {
        var b = Batch();
        var delivery = b.Orders[0].Steps[2];
        void Refused(PawnActivityStep step) => Assert.Throws<DomainException>(() =>
            (b with { Orders = [b.Orders[0] with { Steps = [step] }] }).Validate());
        Refused(new PawnActivityStep(delivery.Kind, delivery.Target, delivery.WorkGiver, delivery.Job, delivery.Count, delivery.Deadline));
        Refused(delivery.At(new Cell(-1, 0)));
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
