using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3775: the construction and activity-task reads name every state a row or binding can be in, and each rides the wire.
/// </summary>
public sealed class ConstructionActivityWireTests
{
    private static ConstructionActivityOption Option(string pawn, ConstructionDelivery delivery, ConstructionFinish finish, StackStorage storage) =>
        new("site", "Wall", new(2, 2), pawn, "WoodLog", "wood", new(1, 1), 5, 75, "FinishFrames", 12, delivery, finish, 5, storage);

    [Fact]
    public void EveryConstructionRowStateRoundTrips()
    {
        ConstructionActivityOption[] rows =
        [
            Option("carried", new ConstructionDelivery.Carried(3, 4), new ConstructionFinish.Finishes(50, ProductiveLearningTests.Work()), new StackStorage.Accepted(0)),
            Option("unsourced", new ConstructionDelivery.Unsourced(), new ConstructionFinish.Finishes(0, null), new StackStorage.Unsourced()),
            Option("cannot-carry", new ConstructionDelivery.CannotCarry(), new ConstructionFinish.CannotFinish(), new StackStorage.Unaccepted()),
            Option("unreachable", new ConstructionDelivery.Unreachable(), new ConstructionFinish.CannotFinish(), new StackStorage.Accepted(7.5)),
        ];
        var clearing = ConstructionFixtures.Clearing("site/clear/cut", PlannedClearingKind.Cut) with { Options = [rows[1]], Targets = ["plant"], Cells = [new(1, 1)] };
        var read = ConstructionFixtures.NoConstruction(100) with
        {
            Options = [.. rows],
            Planned = [.. Enum.GetValues<PlannedSiteVerdict>().Select(v => ConstructionFixtures.Answer("site/" + v, v) with { Options = [rows[0]], Clearing = [clearing] })],
            Removals = [clearing],
        };
        var copy = OrdersJson.Deserialize<ConstructionActivityRead>(OrdersJson.Serialize(read));
        Assert.Equal(read.Options, copy.Options);
        Assert.Equal(read.Planned, copy.Planned);
        Assert.Equal(read.Removals, copy.Removals);
        Assert.Equal(read.Context, copy.Context);
    }

    [Fact]
    public void EveryTaskBindingRoundTrips()
    {
        var step = new PawnActivityStep(PawnActivityKind.Supply, "stack", "", "", 2, 500) { Container = "frame" };
        var read = ConstructionFixtures.NoTasks(300) with
        {
            Bindings = [new ActivityTaskBinding.Bound("bound", step)],
        };
        var copy = OrdersJson.Deserialize<ActivityTaskRead>(OrdersJson.Serialize(read));
        Assert.Equal(read.Bindings, copy.Bindings);

    }

}
