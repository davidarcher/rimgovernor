using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire;
using Xunit;
using System.Text.Json;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodCookingContinuationTests
{
    private static FoodActivityOption Raw(string target = "raw") => new("pawn", target, "Rice", new(3, 4), new(8, 9), "HarvestGiver", 10, 20, 0)
        { NutritionPerUnit = Known(0.05), ApproachTicks = Known(20.0), DeliveryTicks = Known(0.0), HarvestAllowed = Known(true) };
    private static FoodActivityOption Cook(double work = 10) => Raw() with
    {
        Destination = new(5, 6), Cooking = new(new("bench", "bill", "recipe", "cook-giver", new(5, 5), "Meal", 1, new(8, 9)),
            10, work, 5, Known(0.9), false)
    };
    private static FoodActivityRead Read(params FoodActivityOption[] options) => new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From(options) };
    private static PawnActivities Harvest() => StartFoodActivity(Read(Raw("crop")), 1, "method", 500, out _)!;
    private static ActivityTaskRead Done(PawnActivities batch, string state = "completed", params ActivityTaskBinding[] bindings) => new()
    {
        Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 200 }, Bindings = EquatableArray.From(bindings),
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
            new(o.Assignment, o.Method, o.Revision, 0, state, "", null, 200)))),
    };
    private static PawnActivities Select(params FoodActivityOption[] options)
    {
        var harvest = Harvest();
        return ReconsiderObservedFood(harvest, Known(true), Done(harvest), Read(options), out _)!;
    }
    private static PawnActivities Cooking(PawnActivities supply, string ingredient = "changed-stack")
    {
        var task = supply.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe);
        return ContinueCoordinatedActivity(supply, Known(true), Done(supply, bindings:
            [new(task.Id, task.Step with { Recipe = task.Step.Recipe! with { Ingredients = [new(ingredient, 10)] } })]))!;
    }

    [Fact]
    public void RawAndCookingCompeteBeforeRecipeAndIngredientsAreBound()
    {
        var cooked = Select(Raw(), Cook());
        Assert.Equal(3, cooked.Tasks.Count);
        Assert.Equal(PawnActivityKind.Deliver, cooked.Orders[0].Steps[0].Kind);
        Assert.Equal(10, cooked.Orders[0].Steps[0].Count);
        var other = Cook() with { Cooking = Cook().Cooking! with { ProductNutrition = Known(2.0),
            Continuation = Cook().Cooking!.Continuation with { Bill = "other-bill", Recipe = "other-recipe" } } };
        Assert.Equal("other-recipe", Select(Raw(), Cook(), other).Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe).Step.Recipe!.Recipe);
        foreach (var expensive in new[] { Cook(200), Cook() with { TravelTicks = 250 }, Cook() with { Count = 9 },
            Cook() with { Cooking = Cook().Cooking! with { ProductNutrition = default } } })
            Assert.DoesNotContain(Select(Raw(), expensive).Tasks, t => t.Step.Kind == PawnActivityKind.Recipe);
    }

    [Fact]
    public void SupplyRebindsChangedIngredientThenCookingRequiresObservedMealForDelivery()
    {
        var supply = Select(Cook());
        var cooking = Cooking(supply);
        foreach (var batch in new[] { supply, cooking })
        {
            BridgeFunctions.RegisterIntentKinds();
            var action = DomainAction.NewPawnActivitiesAction(new("food-action"), batch);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
            using var json = JsonDocument.Parse(stream.ToArray());
            var decoded = Codecs.Default.Read<DomainAction>(json.RootElement);
            var intent = BridgeFunctions.IntentAction("food-action", decoded).Intent!.PawnActivities!.Batch!;
            Assert.Equal(batch.Tasks, intent.Tasks);
            Assert.Equal(batch.Orders[0].Steps, intent.Orders[0].Steps);
            var wire = WireJson.Deserialize<ActivityTaskRequest>(WireJson.Serialize(new ActivityTaskRequest(new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, batch)));
            Assert.Equal(batch.Tasks, wire.Batch.Tasks);
        }
        var step = cooking.Orders[0].Steps[0];
        Assert.Equal("changed-stack", step.Recipe!.Ingredients.Single().Thing);
        Assert.Equal("recipe", step.Recipe.Recipe);
        var delivery = cooking.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver);
        var delivered = ContinueCoordinatedActivity(cooking, Known(true), Done(cooking, bindings:
            [new(delivery.Id, delivery.Step with { Target = "observed-meal" })]))!;
        Assert.Equal("observed-meal", delivered.Orders[0].Steps[0].Target);
        Assert.Equal(500, delivered.Orders[0].Steps[0].Deadline);
        Assert.True(ContinueCoordinatedActivity(cooking, Known(true), Done(cooking))!.Orders[0].Release);
        Assert.True(ContinueCoordinatedActivity(delivered, Known(true), Done(delivered))!.Orders[0].Release);
    }

    [Fact]
    public void InterruptedCookingLostBenchAndChangedRecipeCannotReplayConsumption()
    {
        var supply = Select(Cook());
        Assert.True(ContinueCoordinatedActivity(supply, Known(true), Done(supply))!.Orders[0].Release);
        var task = supply.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe);
        var wrong = task.Step with { Recipe = task.Step.Recipe! with { Bill = "other-bill" } };
        Assert.True(ContinueCoordinatedActivity(supply, Known(true), Done(supply, bindings: [new(task.Id, wrong)]))!.Orders[0].Release);
        var cooking = Cooking(supply);
        Assert.True(ContinueCoordinatedActivity(cooking, Known(true), Done(cooking, "failed"))!.Orders[0].Release);
        Assert.True(ContinueCoordinatedActivity(cooking, Known(false), Done(cooking))!.Orders[0].Release);
        Assert.Null(ContinueCoordinatedActivity(cooking, Known(true), Done(cooking, "started")));
    }

    [Fact]
    public void ProductiveDurationSurvivesJournalAndFreshMechanicsMustStillSupportNextWork()
    {
        var supply = Select(Cook());
        supply = supply with { Tasks = EquatableArray.From(supply.Tasks.Select(t => t.Step.Kind == PawnActivityKind.Recipe
            ? t with { ProductiveWorkTicks = Known(100d) } : t)) };
        BridgeFunctions.RegisterIntentKinds();
        var action = DomainAction.NewPawnActivitiesAction(new("productive-action"), supply);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Codecs.Default.Write(writer, action);
        using var json = JsonDocument.Parse(stream.ToArray());
        var decoded = Codecs.Default.Read<DomainAction>(json.RootElement);
        var intent = BridgeFunctions.IntentAction("productive-action", decoded).Intent!.PawnActivities!.Batch!;
        Assert.Equal(supply.Tasks, intent.Tasks);
        var recipe = supply.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe);
        var binding = new ActivityTaskBinding(recipe.Id, recipe.Step with { Recipe = recipe.Step.Recipe! with { Ingredients = [new("observed-rice", 10)] } });
        foreach (var estimate in new[] { Unknown<double>(), Known(101d) })
            Assert.True(ContinueCoordinatedActivity(intent, Known(true), Done(intent, bindings: [binding with { ProductiveWorkTicks = estimate }]))!.Orders[0].Release);
        Assert.False(ContinueCoordinatedActivity(intent, Known(true), Done(intent, bindings: [binding with { ProductiveWorkTicks = Known(100d) }]))!.Orders[0].Release);
        var wire = WireJson.Deserialize<ActivityTaskRequest>(WireJson.Serialize(new ActivityTaskRequest(new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, intent)));
        Assert.Equal(intent.Tasks, wire.Batch.Tasks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FreshDemandCanRetireProductiveWorkButUnknownDoesNotCertifyRecovery(bool known)
    {
        var supply = Select(Cook());
        supply = supply with { Tasks = EquatableArray.From(supply.Tasks.Select(t => t.Step.Kind == PawnActivityKind.Recipe
            ? t with { ProductiveWorkTicks = Known(100d) } : t).Append(new("eat", "pawn", [],
                new(PawnActivityKind.Eat, "meal", "", "", 1, 500), new(8, 9), 200))) };
        var read = Done(supply) with { Needs = new() { Tick = 200, Pawns = [new() { Pawn = "pawn", State = "eligible",
            Needs = [new("food", known ? Known(1d) : default, Known(0d), Known(.3), Known(1d))] }] } };
        var need = ReviewProductiveActivityNeed(supply, Known(true), read);
        Assert.Equal(Known(!known), need);
        if (known) Assert.True(ContinueCoordinatedActivity(supply, need, read)!.Orders[0].Release);
        var active = read with { Pawns = EquatableArray.From(read.Pawns.Select(p => p with { Execution = p.Execution! with { State = "started" } })) };
        Assert.Equal(Known(true), ReviewProductiveActivityNeed(supply, Known(true), active));
    }

    [Fact]
    public void CookingChoiceReleasesPreparedHelperButExpensiveRecipeKeepsRawHelper()
    {
        var batch = Harvest();
        var helper = batch.Orders[0] with { Pawn = "helper", Assignment = "method/helper", Preparing = true,
            Steps = [new(PawnActivityKind.Hold, "", "", "", 0, 500)] };
        batch = batch with { Orders = [batch.Orders[0], helper] };
        var done = Done(batch) with { Pawns = EquatableArray.From(Done(batch).Pawns.Select(p => p.Pawn == "helper"
            ? p with { Execution = p.Execution! with { State = "holding" } } : p)) };
        var helperFood = Raw() with { Pawn = "helper", TravelTicks = 25, ApproachTicks = Known(0.0), DeliveryTicks = Known(25.0) };
        var cooking = ReconsiderObservedFood(batch, Known(true), done, Read(helperFood, Cook()), out _)!;
        Assert.True(cooking.Orders.Single(o => o.Pawn == "helper").Release);
        Assert.Contains(cooking.Tasks, t => t.Step.Kind == PawnActivityKind.Recipe);
        var raw = ReconsiderObservedFood(batch, Known(true), done, Read(helperFood, Cook(300)), out _)!;
        Assert.True(raw.Orders.Single(o => o.Pawn == "pawn").Release);
        Assert.Equal("helper", raw.Tasks.Single().Pawn);
    }

    [Fact]
    public void KnownAndUnknownNativeEstimateCollectionsRoundTripWithoutInventingValues()
    {
        var read = Read(Raw(), Raw() with { ApproachTicks = default });
        var decoded = WireJson.Deserialize<FoodActivityRead>(WireJson.Serialize(read));
        Assert.Equal(2, decoded.Options.Count);
        Assert.Equal(20, decoded.Options[0].ApproachTicks.Value);
        Assert.False(decoded.Options[1].ApproachTicks.IsKnown);
        var tasks = new ActivityTaskRead { Known = true, SatisfiedTasks = ["one"],
            Bindings = [new("deliver", new(PawnActivityKind.Deliver, "stack", "", "", 2, 500) { Destination = new(3, 4) }) { RemainingCount = Known(2) }],
            Pawns = [new("pawn", null)], Food = read };
        var restored = WireJson.Deserialize<ActivityTaskRead>(WireJson.Serialize(tasks));
        Assert.Equal(tasks.SatisfiedTasks, restored.SatisfiedTasks);
        Assert.Equal(2, restored.Bindings[0].RemainingCount.Value);
        Assert.Equal("pawn", restored.Pawns[0].Pawn);
        var construction = new ConstructionActivityRead { Known = true, Findings = ["mixed_material_demand_unsupported"],
            Options = [new("site", "Wall", new(2, 2), "pawn", "WoodLog", "wood", new(1, 1), 5, 10, "FinishFrames", Known(2.0), Known(3.0), default)] };
        var sites = WireJson.Deserialize<ConstructionActivityRead>(WireJson.Serialize(construction));
        Assert.Equal(construction.Findings, sites.Findings);
        Assert.False(sites.Options[0].FinishTicks.IsKnown);
        Assert.Equal(3, sites.Options[0].DeliveryTicks.Value);
    }
}
