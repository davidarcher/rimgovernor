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
        Capacity = new ForecastTerm.Assessed(10), Active = true, Busy = true, Ingredients = ThroughputIngredients.Stocked,
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
    public void ChangingNeedDebtDoesNotEraseNativeProductionOrUseTheOldHigherCapacity()
    {
        var start = Start();
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var middle = start with { Tick = start.Tick + GameTime.TicksPerDay / 2,
            Capacity = new ForecastTerm.Assessed(4), Output = new ProductionOutput.Observed("load", 102) };
        (window, _) = WorkRules.CalibrateThroughput(window, middle);
        Assert.Equal(start.Tick, window.Start);
        Assert.Equal(new ForecastTerm.Assessed(4), window.PerDay);
        var (_, result) = WorkRules.CalibrateThroughput(window, start with
        { Tick = start.Tick + GameTime.TicksPerDay, Output = new ProductionOutput.Observed("load", 104) });
        Assert.True(result.Closed);
        Assert.False(result.IsShort);
        Assert.False(result.Widen);
    }

    [Fact]
    public void UnknownCapacityRetainsObservedProductionWithoutClaimingAShortfall()
    {
        var start = Start();
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var unknown = start with { Tick = start.Tick + GameTime.TicksPerDay / 2,
            Capacity = new ForecastTerm.Unassessed("labor_native_work_remaining:worker:hauling"),
            Output = new ProductionOutput.Observed("load", 103) };
        (window, _) = WorkRules.CalibrateThroughput(window, unknown);
        Assert.Equal(start.Tick, window.Start);
        Assert.Equal(100, window.StartProduced);
        Assert.Equal(unknown.Capacity, window.PerDay);
        var (_, result) = WorkRules.CalibrateThroughput(window, start with
        { Tick = start.Tick + GameTime.TicksPerDay, Output = new ProductionOutput.Observed("load", 103) });
        Assert.True(result.Closed);
        Assert.False(result.IsShort);
        Assert.False(result.Widen);
    }

    [Fact]
    public void UnsizedOrdersStillObserveTheirActualOutput()
    {
        var order = new OrderSpec { Recipe = "recipe", BenchKind = "kind", Product = new("meal") };
        var input = new DispatchInputs { Stock = new Dictionary<Resource, long> { [order.Product] = 7 },
            Production = new() { LoadToken = "load", Kills = [], Butchers = [], Counts = [] } };
        var observation = WorkRules.ObserveOrder(order, 0, new DispatchSizing.Unsized(), input);
        Assert.IsType<ForecastTerm.Unassessed>(observation.Capacity);
        Assert.Equal(new ProductionOutput.Observed("load", 0), observation.Output);
        Assert.Equal(7, observation.Stock);
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
    public void DispatcherRetainsAnUnsizedOrdersWindowAndDropsUnsupportedWidening()
    {
        var order = new OrderSpec { Recipe = "recipe", BenchKind = "kind", Product = new("meal"), Mode = BillMode.StockTarget, Target = 10 };
        var bill = new GearBill { Observed = BillWire.Bill("bill") with { Recipe = order.Recipe }, Spec = order };
        var input = new DispatchInputs { Tick = 100, Declared = [new() { Orders = [order] }],
            Benches = [new() { Observed = ProductionBenchFixtures.Observed("bench", "kind", "token", true), Bills = [bill], Recipes = [] }],
            Production = new() { LoadToken = "load", Kills = [], Butchers = [], Counts = [Count(Obs.DeliveryKind.Recipe, "bill", "meal", 3)] } };
        var memory = new DispatchMemory();
        memory.Dispatch(input);
        var original = memory.Windows[order.Key()];
        Assert.True(original.Open);
        memory.Widen[order.Key()] = 1;
        memory.Dispatch(input with { Tick = 200, Production = input.Production with
            { Counts = [Count(Obs.DeliveryKind.Recipe, "bill", "meal", 5)] } });
        Assert.Equal(original.Start, memory.Windows[order.Key()].Start);
        Assert.Equal(3, memory.Windows[order.Key()].StartProduced);
        Assert.IsType<ForecastTerm.Unassessed>(memory.Windows[order.Key()].PerDay);
        Assert.Empty(memory.Widen);
        Assert.Empty(memory.Shortfalls);
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
        var observed = WorkRules.ObserveOrder(order, 1, new DispatchSizing.Sized(1, 1), input);
        Assert.Equal(new ProductionOutput.Observed("load", 3), observed.Output);
        Assert.Equal(new ProductionOutput.CensusUnread(),
            WorkRules.ObserveOrder(order, 1, new DispatchSizing.Sized(1, 1), input with { Production = null }).Output);
    }

    [Theory]
    [InlineData("Refuel", null, false)]
    [InlineData("Repair", null, false)]
    [InlineData("HaulToContainer", null, false)]
    [InlineData("DoBill", "other-bill", false)]
    [InlineData("DoBill", "bill", true)]
    public void BenchTargetAloneDoesNotEstablishWorkOnTheRequestedBill(string job, string? billId, bool busy)
    {
        var order = new OrderSpec { Recipe = "recipe", BenchKind = "kind", Product = new("meal"), Target = 1000 };
        var bill = new GearBill { Observed = BillWire.Bill("bill") with { Recipe = order.Recipe }, Spec = order };
        var worker = PawnFixtures.Worker("worker");
        worker = worker with { Observed = worker.Observed with { Job = new()
        {
            DefName = job, BillId = billId,
            TargetA = new() { Target = new Obs.TargetRefTarget.EntityCase(new() { Id = "bench" }, new Obs.EntityPlace.Unplaced()) },
        } } };
        var input = new DispatchInputs
        {
            Tick = 100, Pawns = [worker],
            Benches = [new() { Observed = ProductionBenchFixtures.Observed("bench", "kind", "token", true),
                Bills = [bill], Recipes = [new() { Access = ProductionBenchFixtures.Recipe(order.Recipe),
                    RequiredWork = [], WorkAmount = 1, Ingredients = [] }] }],
            Production = new() { LoadToken = "load", Kills = [], Butchers = [], Counts = [] },
        };
        var observation = WorkRules.ObserveOrder(order, 2, new DispatchSizing.Sized(10, 10), input);
        Assert.Equal("bench", worker.Job.Target.Thing);
        Assert.Equal(busy, observation.Busy);
        Assert.Equal(new ProductionOutput.Observed("load", 0), observation.Output);
        var (window, _) = WorkRules.CalibrateThroughput(new(), observation);
        var (_, result) = WorkRules.CalibrateThroughput(window, observation with { Tick = input.Tick + GameTime.TicksPerDay });
        Assert.True(result.IsShort);
        Assert.Equal(busy ? UnmetReason.None : UnmetReason.WorkUnstarted, result.Reason);
        Assert.Equal(busy, result.Widen);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void OpeningSampleIsPartOfTheEvidenceWindow(bool busyAtStart, bool activeAtStart, bool widen)
    {
        var start = Start() with { Busy = busyAtStart, Active = activeAtStart };
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var (_, result) = WorkRules.CalibrateThroughput(window, start with
            { Tick = start.Tick + GameTime.TicksPerDay, Busy = false, Active = true });
        Assert.True(result.IsShort);
        Assert.Equal(widen ? UnmetReason.None : UnmetReason.WorkUnstarted, result.Reason);
        Assert.Equal(widen, result.Widen);
    }

    private static Obs.DeliveryCount Count(Obs.DeliveryKind kind, string source, string product, long units) => new()
    { Kind = kind, Source = source, Def = product, Units = units, Nutrition = 0 };
}
