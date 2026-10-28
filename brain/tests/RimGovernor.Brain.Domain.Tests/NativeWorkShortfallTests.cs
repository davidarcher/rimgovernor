using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeWorkShortfallTests
{
    private static ForecastCoupledContext Context(ForecastCapacityWindows windows) => ForecastValueTests.Context(ForecastValueTests.Pawn()) with
    {
        Activities = new() { Deadline = 100, Activities = [new("haul", "source", "destination", [])],
            Pawns = [new("worker", "source", windows, 1)], Options = [new("haul", "worker", 80, 0)] },
        Bindings = new Dictionary<(string Task, string Pawn), ActivityTaskCandidate>
        { [("haul", "worker")] = new("haul", "worker", [], PawnActivityKind.Deliver, "stack", "", "", 1, 100, new(0, 0)) },
        OptionWork = new Dictionary<(string Activity, string Pawn), double> { [("haul", "worker")] = 80 },
    };

    private static WorkShortfall Shortfall(ForecastCoupledContext context, params ForecastActivityAllocation[] allocations) =>
        WorkShortfallRules.Of(context, [new("root", []) { ActivityCompletions = ["haul"] }],
            new HashSet<string>(), allocations, 100, new() { Hauling = "Hauling" },
            new Dictionary<string, IReadOnlyDictionary<string, double>>());

    [Fact]
    public void AnOmittedServiceCounterfactualDoesNotCreateProductiveDemand()
    {
        var context = Context(new ForecastCapacityWindows.Read([new(0, 100)]));
        context = context with
        {
            Activities = context.Activities with { Activities = [.. context.Activities.Activities, new("eat", "destination", "destination", ["haul"])] },
            NeedTasks = [new("eat", "worker", "food", "meal", new("Meal"), 1, .5, [])],
        };
        var result = WorkShortfallRules.Of(context, [new("service", []) { ActivityCompletions = ["eat"] }],
            new HashSet<string>(), [], 100, new() { Hauling = "Hauling" }, new Dictionary<string, IReadOnlyDictionary<string, double>>());
        Assert.Empty(result.Rows);
        Assert.Empty(result.Unassessed);
        // An independently demanded delivery still competes for capacity; a service alternative does not erase it.
        Assert.NotEmpty(Shortfall(context with { Activities = context.Activities with
            { Pawns = [new("worker", "source", new ForecastCapacityWindows.Read([new(60, 100)]), 1)] } }).Rows);
    }

    [Fact]
    public void NativeOccupiedTimeCannotFinanceUnselectedWork()
    {
        var context = Context(new ForecastCapacityWindows.Read([new(60, 100)]));
        var row = Assert.Single(Shortfall(context).Rows);
        Assert.Equal(40 * GameTime.TicksPerDay / 100d, row.TicksPerDay, 9);
        Assert.Empty(Shortfall(Context(new ForecastCapacityWindows.Read([new(0, 100)]))).Rows);
    }

    [Fact]
    public void OverlappingAllocationsAndCapacityWindowsOnlyCountAvailableTimeOnce()
    {
        var context = Context(new ForecastCapacityWindows.Read([new(0, 50), new(30, 100)]));
        var row = Assert.Single(Shortfall(context,
            new("other-one", "worker", 10, 10, 10, 40, 0, 30, 30, 0),
            new("other-two", "worker", 20, 20, 20, 60, 0, 40, 40, 0)).Rows);
        Assert.Equal(30 * GameTime.TicksPerDay / 100d, row.TicksPerDay, 9);
    }

    [Fact]
    public void UntimedNativeWorkLeavesShortfallUnassessedRatherThanAssumingAnIdleDay()
    {
        var shortfall = Shortfall(Context(new ForecastCapacityWindows.Unread()));
        Assert.Equal(["native_work_capacity:worker"], shortfall.Unassessed);
        Assert.Throws<RequiredFactException>(() => PawnValueRules.WorkAdded(new("another") { FreeTicksPerDay = 100 }, shortfall));
        var attrs = RimGovernor.Brain.Runtime.ColonyReview.PawnValueAttrs(new() { WorkShortfall = shortfall }).ToDictionary(a => a.Key, a => a.Value);
        Assert.Null(attrs[RimGovernor.Brain.Telemetry.ReviewTelemetry.PawnValuesKey]);
        var evidence = Assert.IsType<Dictionary<string, object?>>(attrs[RimGovernor.Brain.Telemetry.ReviewTelemetry.WorkShortfallKey]);
        Assert.Equal("native_work_capacity:worker", Assert.Single(Assert.IsType<string[]>(evidence["unassessed"])));
    }

    [Fact]
    public void AnUnknownPawnWithNoOptionOnTheDeferredWorkDoesNotInvalidateTheEstimate()
    {
        var context = Context(new ForecastCapacityWindows.Read([new(0, 100)]));
        context = context with { Activities = context.Activities with { Pawns = [.. context.Activities.Pawns,
            new("dependent", "source", new ForecastCapacityWindows.Unread(), 1)] } };
        var result = Shortfall(context);
        Assert.Empty(result.Unassessed);
        Assert.Empty(result.Rows);
    }
}
