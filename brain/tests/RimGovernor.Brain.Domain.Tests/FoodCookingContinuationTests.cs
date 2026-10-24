using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire;
using Xunit;
using System.Text.Json;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodCookingContinuationTests
{
    private static FoodActivityOption Raw(string target = "raw") => new("pawn", target, "Rice", new(3, 4), new(8, 9), "HarvestGiver", 10, 0)
        { SkillWork = null, SourceCount = 10, ApproachTicks = 20.0, DeliveryTicks = 0.0, HarvestAllowed = true };
    private static FoodActivityRead Read(params FoodActivityOption[] options) => new() { Options = EquatableArray.From(options) };
    private static ActivityTaskRead Done(PawnActivities batch, string state = "completed", params ActivityTaskBinding[] bindings) => ConstructionFixtures.NoTasks() with
    {
        Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 200 }, Bindings = EquatableArray.From(bindings),
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
            new(o.Assignment, o.Method, o.Revision, 0, state, "", 200)))),
    };
    private static PawnActivities Cooking(PawnActivities supply, string ingredient = "changed-stack")
    {
        var task = supply.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe);
        return CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, ActivityNeed.Owed, Done(supply, bindings:
            [new ActivityTaskBinding.Bound(task.Id, task.Step with { Recipe = task.Step.Recipe! with { Ingredients = [new(ingredient, 10)] } })]))!;
    }

    [Fact]
    public void SupplyRebindsChangedIngredientThenCookingRequiresObservedMealForDelivery()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        var cooking = Cooking(supply);
        foreach (var batch in new[] { supply, cooking })
        {
            IntentActionRules.RegisterIntentKinds();
            var action = DomainAction.NewPawnActivitiesAction(new("food-action"), batch);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
            using var json = JsonDocument.Parse(stream.ToArray());
            var decoded = Codecs.Default.Read<DomainAction>(json.RootElement);
            var intent = IntentActionRules.IntentAction("food-action", decoded).Intent!.PawnActivities!.Batch!;
            Assert.Equal(batch.Tasks, intent.Tasks);
            Assert.Equal(batch.Orders[0].Steps, intent.Orders[0].Steps);
            var wire = WireJson.Deserialize<ActivityTaskRequest>(WireJson.Serialize(new ActivityTaskRequest(new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, batch)));
            Assert.Equal(batch.Tasks, wire.Batch.Tasks);
        }
        var step = cooking.Orders[0].Steps[0];
        Assert.Equal("changed-stack", step.Recipe!.Ingredients.Single().Thing);
        Assert.Equal("recipe", step.Recipe.Recipe);
        var delivery = cooking.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver);
        var delivered = CoordinatedActivitiesRules.ContinueCoordinatedActivity(cooking, ActivityNeed.Owed, Done(cooking, bindings:
            [new ActivityTaskBinding.Bound(delivery.Id, delivery.Step with { Target = "observed-meal" })]))!;
        Assert.Equal("observed-meal", delivered.Orders[0].Steps[0].Target);
        Assert.Equal(500, delivered.Orders[0].Steps[0].Deadline);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(cooking, ActivityNeed.Owed, Done(cooking))!.Orders[0].Release);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(delivered, ActivityNeed.Owed, Done(delivered))!.Orders[0].Release);
    }

    [Fact]
    public void InterruptedCookingLostBenchAndChangedRecipeCannotReplayConsumption()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, ActivityNeed.Owed, Done(supply))!.Orders[0].Release);
        var task = supply.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe);
        var wrong = task.Step with { Recipe = task.Step.Recipe! with { Bill = "other-bill" } };
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, ActivityNeed.Owed, Done(supply, bindings: [new ActivityTaskBinding.Bound(task.Id, wrong)]))!.Orders[0].Release);
        var cooking = Cooking(supply);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(cooking, ActivityNeed.Owed, Done(cooking, "failed"))!.Orders[0].Release);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(cooking, ActivityNeed.Ended, Done(cooking))!.Orders[0].Release);
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(cooking, ActivityNeed.Owed, Done(cooking, "started")));
    }

    [Fact]
    public void ProductiveDurationSurvivesJournalAndFreshMechanicsMustStillSupportNextWork()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        supply = supply with { Tasks = EquatableArray.From(supply.Tasks.Select(t => t.Step.Kind == PawnActivityKind.Recipe
            ? new BoundRecipeTask(t, 100d) : t)) };
        IntentActionRules.RegisterIntentKinds();
        var action = DomainAction.NewPawnActivitiesAction(new("productive-action"), supply);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var json = JsonDocument.Parse(stream.ToArray());
        var decoded = Codecs.Default.Read<DomainAction>(json.RootElement);
        var intent = IntentActionRules.IntentAction("productive-action", decoded).Intent!.PawnActivities!.Batch!;
        Assert.Equal(supply.Tasks, intent.Tasks);
        var recipe = supply.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe);
        var step = recipe.Step with { Recipe = recipe.Step.Recipe! with { Ingredients = [new("observed-rice", 10)] } };
        // A bound recipe task needs the pawn's fresh recipe ticks: a binding without them, or slower, retires the work.
        foreach (var binding in new ActivityTaskBinding[] { new ActivityTaskBinding.Bound(recipe.Id, step), new ActivityTaskBinding.Recipe(recipe.Id, step, 101d) })
            Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(intent, ActivityNeed.Owed, Done(intent, bindings: [binding]))!.Orders[0].Release);
        Assert.False(CoordinatedActivitiesRules.ContinueCoordinatedActivity(intent, ActivityNeed.Owed, Done(intent, bindings: [new ActivityTaskBinding.Recipe(recipe.Id, step, 100d)]))!.Orders[0].Release);
        var wire = WireJson.Deserialize<ActivityTaskRequest>(WireJson.Serialize(new ActivityTaskRequest(new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, intent)));
        Assert.Equal(intent.Tasks, wire.Batch.Tasks);
    }

    [Fact]
    public void FreshDemandCanRetireProductiveWork()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        supply = supply with { Tasks = EquatableArray.From(supply.Tasks.Select(t => t.Step.Kind == PawnActivityKind.Recipe
            ? new BoundRecipeTask(t, 100d) : t).Append(new("eat", "pawn", [],
                new(PawnActivityKind.Eat, "meal", "", "", 1, 500), new(8, 9), 200))) };
        var read = Done(supply) with { Needs = NeedsFixtures.Empty with { Tick = new(200), Pawns = [NeedsFixtures.Pawn() with { Pawn = "pawn", State = "eligible",
            Needs = [NeedsFixtures.Rate("food", 1d, 0d, .3)] }] } };
        var need = ForecastServiceInvestments.ReviewServiceActivityNeed(supply, ActivityNeed.Owed, read);
        Assert.Equal(ActivityNeed.Ended, need);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, need, read)!.Orders[0].Release);
        var active = read with { Pawns = EquatableArray.From(read.Pawns.Select(p => p with { Execution = p.Execution! with { State = "started" } })) };
        Assert.Equal(ActivityNeed.Owed, ForecastServiceInvestments.ReviewServiceActivityNeed(supply, ActivityNeed.Owed, active));
    }

    [Fact]
    public void NativeEstimateCollectionsRoundTripWithoutInventingValues()
    {
        var read = Read(Raw(), Raw() with { ApproachTicks = 0 });
        var decoded = WireJson.Deserialize<FoodActivityRead>(WireJson.Serialize(read));
        Assert.Equal(read, decoded);
        var tasks = ConstructionFixtures.NoTasks() with { SatisfiedTasks = ["one"],
            Bindings = [new ActivityTaskBinding.Supply("deliver", new(PawnActivityKind.Deliver, "stack", "", "", 2, 500) { Destination = new(3, 4) }, 2)],
            Pawns = [new("pawn", null)] };
        var restored = WireJson.Deserialize<ActivityTaskRead>(WireJson.Serialize(tasks));
        Assert.Equal(tasks.SatisfiedTasks, restored.SatisfiedTasks);
        Assert.Equal(2, Assert.IsType<ActivityTaskBinding.Supply>(restored.Bindings[0]).RemainingCount);
        Assert.Equal("pawn", restored.Pawns[0].Pawn);
        var construction = ConstructionFixtures.NoConstruction() with { Findings = ["mixed_material_demand_unsupported"],
            Options = [new Row("site", "Wall", new(2, 2), "pawn", "WoodLog", "wood", new(1, 1), 5, 10, "FinishFrames", Known(2.0), Known(3.0), default)] };
        var sites = WireJson.Deserialize<ConstructionActivityRead>(WireJson.Serialize(construction));
        Assert.Equal(construction.Findings, sites.Findings);
        Assert.IsType<ConstructionFinish.CannotFinish>(sites.Options[0].Finish);
        Assert.Equal(new ConstructionDelivery.Carried(2, 3), sites.Options[0].Delivery);
    }
}
