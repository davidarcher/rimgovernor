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
        BillWork = Obs.BillWorkEvidence.RecipeWorkObserved,
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
        Assert.Equal(busy ? UnmetReason.RecipeWorkUnassessed : UnmetReason.WorkUnstarted, result.Reason);
        Assert.False(result.Widen);
    }

    [Theory]
    [InlineData(Obs.BillWorkEvidence.Unassessed, "bill", UnmetReason.RecipeWorkUnassessed, false)]
    [InlineData(Obs.BillWorkEvidence.IngredientsPending, "bill", UnmetReason.DeliveryBound, false)]
    [InlineData(Obs.BillWorkEvidence.RecipeWorkObserved, "bill", UnmetReason.None, true)]
    [InlineData(Obs.BillWorkEvidence.RecipeWorkObserved, "other-bill", UnmetReason.WorkUnstarted, false)]
    public void ExactBillEvidenceDistinguishesPendingIngredientsFromObservedRecipeWork(
        Obs.BillWorkEvidence evidence, string billId, UnmetReason reason, bool widen)
    {
        var order = new OrderSpec { Recipe = "recipe", BenchKind = "kind", Product = new("meal"), Target = 1000,
            Mode = BillMode.StockTarget, Class = ResourceClass.Food };
        var worker = PawnFixtures.Worker("worker");
        worker = worker with { Work = PawnFixtures.WorkTable(true, [WorkPriority.Planned(WorkType.Cooking, 1)]),
            Observed = worker.Observed with { Job = new() { BillId = billId, BillWork = evidence, WorkTypeDefName = Wire.Name(WorkType.Cooking) },
                Settings = worker.Observed.Settings with { Schedule = EquatableArray.From(Enumerable.Range(0, 24)
                    .Select(hour => new Obs.TimetableSlot { Hour = (uint)hour, AssignmentDefName = PawnRules.ScheduleAnything })) } } };
        var input = new DispatchInputs { Tick = 100, Pawns = [worker],
            Declared = [new() { Orders = [order] }],
            Needs = NeedsFixtures.Read(100, NeedsFixtures.Pawn("worker") with { Needs = [] }),
            Benches = [new() { Observed = ProductionBenchFixtures.Observed("bench", "kind", "token", true),
                Bills = [new() { Observed = BillWire.Bill("bill") with { Recipe = order.Recipe }, Spec = order }],
                Recipes = [new() { Access = ProductionBenchFixtures.Recipe(order.Recipe), RequiredWork = [new() { Work = WorkType.Cooking }], WorkAmount = 1, Ingredients = [] }] }],
            Production = new() { LoadToken = "load", Kills = [], Butchers = [], Counts = [] } };
        input = input with { Benches = [input.Benches[0], input.Benches[0] with
            { Observed = ProductionBenchFixtures.Observed("spare-bench", "kind", "token", true), Bills = [] }] };
        var observed = WorkRules.ObserveOrder(order, 2, new DispatchSizing.Sized(10, 10), input);
        var (window, _) = WorkRules.CalibrateThroughput(new(), observed);
        var (_, result) = WorkRules.CalibrateThroughput(window, observed with { Tick = 100 + GameTime.TicksPerDay });
        Assert.True(result.IsShort);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(widen, result.Widen);
        Assert.Equal(new ProductionOutput.Observed("load", 0), observed.Output);
        // Exercise the live dispatch-memory reader too: the evidence changes actual bill-copy
        // allocation on the next review, rather than stopping at a standalone calibration result.
        var memory = new DispatchMemory();
        Assert.Equal(1, memory.Dispatch(input).Copies[order.Key()]);
        memory.Dispatch(input with { Tick = input.Tick + GameTime.TicksPerDay });
        Assert.Equal(widen ? 2 : 1, memory.Dispatch(input with { Tick = input.Tick + GameTime.TicksPerDay + 1 }).Copies[order.Key()]);
        if (!widen) Assert.Equal(reason, memory.Shortfalls[order.Key()].Reason);
    }

    [Fact]
    public void RecipeWorkEvidenceSurvivesLaterGatheringButResetsWithTheProductionBasis()
    {
        var start = Start();
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var next = start with { Tick = start.Tick + GameTime.TicksPerDay,
            BillWork = Obs.BillWorkEvidence.IngredientsPending };
        var (_, result) = WorkRules.CalibrateThroughput(window, next);
        Assert.True(result.Widen);
        var (reset, discarded) = WorkRules.CalibrateThroughput(window, next with { Basis = next.Basis! with { Order = new() { Target = 2 } } });
        Assert.False(discarded.Closed);
        Assert.Equal(1, reset.GatheringSamples);
        Assert.Equal(0, reset.RecipeWorkSamples);
        var (_, fresh) = WorkRules.CalibrateThroughput(reset, next with { Tick = next.Tick + GameTime.TicksPerDay, Basis = reset.Basis });
        Assert.Equal(UnmetReason.DeliveryBound, fresh.Reason);
        Assert.False(fresh.Widen);
    }

    // Samples one evidence per tick across a day, then closes the window with a final sample of the last evidence.
    private static Calibration CloseWindow(Obs.BillWorkEvidence?[] samples, ThroughputIngredients ingredients = ThroughputIngredients.Stocked)
    {
        var start = Start() with { Ingredients = ingredients };
        ThroughputObservation At(int i) => start with
        {
            Tick = start.Tick + (i == samples.Length - 1 ? GameTime.TicksPerDay : i), Busy = samples[i] is not null,
            BillWork = samples[i] ?? Obs.BillWorkEvidence.Unassessed,
        };
        var window = new ThroughputWindow();
        Calibration result = new();
        for (var i = 0; i < samples.Length; i++) (window, result) = WorkRules.CalibrateThroughput(window, At(i));
        Assert.True(result.Closed);
        Assert.True(result.IsShort);
        return result;
    }

    [Fact]
    public void GatheringDominatedBusySamplesAreDeliveryBoundAndDoNotWiden()
    {
        const Obs.BillWorkEvidence G = Obs.BillWorkEvidence.IngredientsPending, R = Obs.BillWorkEvidence.RecipeWorkObserved;
        var result = CloseWindow([G, G, R, G, G, G, G, G]);
        Assert.Equal(UnmetReason.DeliveryBound, result.Reason);
        Assert.False(result.Widen);
    }

    [Fact]
    public void RecipeWorkInMostBusySamplesStillWidens()
    {
        const Obs.BillWorkEvidence G = Obs.BillWorkEvidence.IngredientsPending, R = Obs.BillWorkEvidence.RecipeWorkObserved;
        var result = CloseWindow([G, R, R, G, R, R, R, R]);
        Assert.Equal(UnmetReason.None, result.Reason);
        Assert.True(result.Widen);
    }

    [Fact]
    public void IdleStockedBenchIsWorkUnstartedNotDeliveryBound()
    {
        var result = CloseWindow([null, null, null]);
        Assert.Equal(UnmetReason.WorkUnstarted, result.Reason);
        Assert.False(result.Widen);
    }

    [Fact]
    public void UnassessedOnlyEvidenceStaysUnassessed()
    {
        var result = CloseWindow([Obs.BillWorkEvidence.Unassessed, Obs.BillWorkEvidence.Unassessed, Obs.BillWorkEvidence.Unassessed]);
        Assert.Equal(UnmetReason.RecipeWorkUnassessed, result.Reason);
        Assert.False(result.Widen);
    }

    [Fact]
    public void GatheringBelowTheShareWithoutRecipeWorkStaysIngredientsPending()
    {
        const Obs.BillWorkEvidence G = Obs.BillWorkEvidence.IngredientsPending, U = Obs.BillWorkEvidence.Unassessed;
        var result = CloseWindow([G, U, U, G]);
        Assert.Equal(UnmetReason.IngredientsPending, result.Reason);
        Assert.False(result.Widen);
    }

    [Theory]
    [InlineData(true, true, UnmetReason.None, true)]
    [InlineData(false, false, UnmetReason.RecipeWorkUnassessed, false)]
    [InlineData(false, true, UnmetReason.WorkUnstarted, false)]
    public void OpeningSampleIsPartOfTheEvidenceWindow(bool busyAtStart, bool activeAtStart, UnmetReason reason, bool widen)
    {
        var start = Start() with { Busy = busyAtStart, Active = activeAtStart };
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var (_, result) = WorkRules.CalibrateThroughput(window, start with
            { Tick = start.Tick + GameTime.TicksPerDay, Busy = false, Active = true });
        Assert.True(result.IsShort);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(widen, result.Widen);
    }

    private static Obs.DeliveryCount Count(Obs.DeliveryKind kind, string source, string product, long units) => new()
    { Kind = kind, Source = source, Def = product, Units = units, Nutrition = 0 };
}
