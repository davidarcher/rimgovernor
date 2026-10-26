using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using Obs = RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeThroughputTests
{
    private static ThroughputObservation Start() => new()
    {
        Tick = 100, Stock = 20, Target = 1000, Output = new ProductionOutput.Observed("load", 100), Carrying = 1, Eligible = 2,
        Capacity = 10, Active = true, Busy = true, Ingredients = ThroughputIngredients.Stocked,
        Basis = new(new(), [new("bench", true, 1, null, [new("bill", true, "")])],
            [new(new("worker"), true, true, [], [], [])]),
    };

    [Theory]
    [InlineData(0, 110, false)] // Output was eaten or otherwise consumed.
    [InlineData(120, 100, true)] // Delivery/import increased stock without recipe production.
    [InlineData(20, 110, false)] // Production and consumption balanced.
    public void ProductionIsIndependentOfDeliveryAndConsumption(long stock, long produced, bool shortfall)
    {
        var start = Start();
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var (_, result) = WorkRules.CalibrateThroughput(window, start with
        { Tick = start.Tick + GameTime.TicksPerDay, Stock = stock, Output = new ProductionOutput.Observed("load", produced) });
        Assert.True(result.Closed);
        Assert.Equal(shortfall, result.IsShort);
        Assert.Equal(shortfall ? 10 : 0, result.ShortPerDay);
    }

    [Theory]
    [InlineData("load")]
    [InlineData("rewind")]
    [InlineData("counter")]
    [InlineData("order")]
    [InlineData("rate")]
    [InlineData("worker")]
    [InlineData("bench")]
    [InlineData("bill")]
    [InlineData("priority")]
    [InlineData("speed")]
    public void RelevantChangesRebaselineInsteadOfComparingStaleProduction(string change)
    {
        var start = Start();
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var next = start with { Tick = start.Tick + GameTime.TicksPerDay };
        next = change switch
        {
            "load" => next with { Output = new ProductionOutput.Observed("new-load", 100) },
            "rewind" => next with { Tick = 1 },
            "counter" => next with { Output = new ProductionOutput.Observed("load", 0) },
            "order" => next with { Basis = next.Basis! with { Order = new() { Target = 2 } } },
            "rate" => next with { Capacity = 20 },
            "worker" => next with { Basis = next.Basis! with
            { Workers = [new(new("worker"), false, true, [], [], [])] } },
            "bench" => next with { Basis = next.Basis! with
            { Benches = [next.Basis.Benches[0] with { Speed = 2 }] } },
            "bill" => next with { Basis = next.Basis! with
            { Benches = [next.Basis.Benches[0] with { Bills = [new("replacement-bill", true, "")] }] } },
            "priority" => next with { Basis = next.Basis! with
            { Workers = [next.Basis.Workers[0] with { Priorities = [WorkPriority.Planned(WorkType.Cooking, 1)] }] } },
            "speed" => next with { Basis = next.Basis! with
            { Workers = [next.Basis.Workers[0] with { Speeds = [new("observed-speed", 2)] }] } },
            _ => throw new ArgumentException(change),
        };
        var (fresh, result) = WorkRules.CalibrateThroughput(window, next);
        Assert.False(result.Closed);
        Assert.Equal(next.Tick, fresh.Start);
        Assert.Equal(Assert.IsType<ProductionOutput.Observed>(next.Output).Units, fresh.StartProduced);
        Assert.Equal(next.Basis, fresh.Basis);
    }

    [Fact]
    public void MissingProductionIsUnavailableRatherThanZero()
    {
        var start = Start();
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var (unread, result) = WorkRules.CalibrateThroughput(window, start with
        { Tick = start.Tick + GameTime.TicksPerDay, Output = new ProductionOutput.CensusUnread() });
        Assert.False(unread.Open);
        Assert.False(result.Closed);
        Assert.False(result.Widen);
    }

    [Fact]
    public void OrderReadsOnlyItsExactBillAndProductFromCreationCounters()
    {
        var order = new OrderSpec { Recipe = "recipe", BenchKind = "kind", Product = new("meal") };
        var bill = new GearBill { Observed = BillWire.Bill("bill") with { Recipe = order.Recipe }, Spec = order };
        var input = new DispatchInputs
        {
            Benches = [new() { Observed = ProductionBenchFixtures.Observed("bench", "kind", "token", true),
                Bills = [bill], Recipes = [] }],
            Production = new() { LoadToken = "load", Kills = [], Butchers = [], Counts =
            [Count(Obs.DeliveryKind.Recipe, "bill", "meal", 3),
             Count(Obs.DeliveryKind.Recipe, "another-bill", "meal", 7),
             Count(Obs.DeliveryKind.Recipe, "bill", "other-product", 11),
             Count(Obs.DeliveryKind.Crop, "bill", "meal", 13)] },
        };
        var observed = WorkRules.ObserveOrder(order, 1, new(0, 1, 1), input);
        Assert.Equal(new ProductionOutput.Observed("load", 3), observed.Output);
        Assert.Equal(new ProductionOutput.CensusUnread(),
            WorkRules.ObserveOrder(order, 1, new(0, 1, 1), input with { Production = null }).Output);
    }

    private static Obs.DeliveryCount Count(Obs.DeliveryKind kind, string source, string product, long units) => new()
    { Kind = kind, Source = source, Def = product, Units = units, Nutrition = 0 };
}
