using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;


using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodCookingContinuationTests
{
    private static FoodActivityOption Raw(string target = "raw") => new("pawn", target, "Rice", new(3, 4), new(8, 9), "HarvestGiver", 10, 0)
        { SkillWork = null, SourceCount = 10, ApproachTicks = 20.0, DeliveryTicks = 0.0, HarvestAllowed = true };
    private static FoodActivityRead Read(params FoodActivityOption[] options) => new() { Options = EquatableArray.From(options) };
    private static ActivityTaskRead Done(PawnActivities batch, string state = "completed", params ActivityTaskBinding[] bindings) => ConstructionFixtures.NoTasks() with
    {
        Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 200 }, Bindings = EquatableArray.From(bindings),
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
            new(o.Assignment, o.Method, o.Revision, 0, state, "", 200)))),
    };
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public void OldRecipeChainReturnsToNativeWithoutRebindingInputsOrCreditingMeals(string state)
    {
        var batch = ActivityContinuationFixtures.Cooking();
        var read = Done(batch, state);
        var next = Assert.IsType<PawnActivities>(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read));
        Assert.True(Assert.Single(next.Orders).Release);
        Assert.Empty(next.Tasks);
        Assert.Empty(read.SatisfiedTasks);
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Done(batch, "started")));
    }

    [Fact]
    public void FreshDemandCanRetireProductiveWork()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        supply = supply with { Tasks = EquatableArray.From(supply.Tasks.Append(new("eat", "pawn", [],
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
        var decoded = OrdersJson.Deserialize<FoodActivityRead>(OrdersJson.Serialize(read));
        Assert.Equal(read, decoded);
        var tasks = ConstructionFixtures.NoTasks() with { SatisfiedTasks = ["one"],
            Bindings = [new ActivityTaskBinding.Supply("deliver", new PawnActivityStep(PawnActivityKind.Deliver, "stack", "", "", 2, 500).At(new(3, 4)), 2)],
            Pawns = [new("pawn", null)] };
        var restored = OrdersJson.Deserialize<ActivityTaskRead>(OrdersJson.Serialize(tasks));
        Assert.Equal(tasks.SatisfiedTasks, restored.SatisfiedTasks);
        Assert.Equal(2, Assert.IsType<ActivityTaskBinding.Supply>(restored.Bindings[0]).RemainingCount);
        Assert.Equal("pawn", restored.Pawns[0].Pawn);
        var construction = ConstructionFixtures.NoConstruction() with { Findings = ["mixed_material_demand_unsupported"],
            Options = [new Row("site", "Wall", new(2, 2), "pawn", "WoodLog", "wood", new(1, 1), 5, 10, "FinishFrames", 2.0, 3.0, default)] };
        var sites = OrdersJson.Deserialize<ConstructionActivityRead>(OrdersJson.Serialize(construction));
        Assert.Equal(construction.Findings, sites.Findings);
        Assert.IsType<ConstructionFinish.CannotFinish>(sites.Options[0].Finish);
        Assert.Equal(new ConstructionDelivery.Carried(2, 3), sites.Options[0].Delivery);
    }
}
