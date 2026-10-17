using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Observation;
using RimGovernor.Contracts.Wire;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using O = RimGovernor.Contracts.Wire.Observations;
using Ops = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class UnfinishedMaterialTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static readonly Resource Alternate = new("TestAlternate");
    private static ProductionBatch Batch => new(new ProductionBill("bench", "TestRecipe", BillMode.GearBatch, 3, Material.Value),
        new ProgressView { Action = new ActionId("batch"), Stage = Stage.Completed, Attempt = new AttemptId(1), Bill = Known("bill") })
    { Slots = Known<EquatableArray<EquatableArray<Amount>>>([[new(Material, 10)]]), UsesUnfinishedItem = Known(true) };
    private static ProductionBatchState State(int remaining, string item = "unfinished") => new("bill", Known(true), Known(remaining), Known(false), Known(false))
    { UnfinishedWork = Known(new ProductionUnfinishedWork(item, item.Length == 0 ? [] : [new(Material, 10)])) };
    private static Fact<EquatableArray<ForecastCommitment>> Promises(ProductionBatchState state, ProductionBatch? batch = null) =>
        ProductionPromisesOf(Known<EquatableArray<ProductionBatch>>([batch ?? Batch]), Known<EquatableArray<ProductionBatchState>>([state]));
    private static RoundsFacts Facts(ProductionBatchState state, long loose = 90) => new()
    {
        ProductionPromises = Promises(state), BillReservations = Known(default(EquatableArray<IngredientReservation>)),
        ConstructionDeficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [Material] = 40 }),
        Resources = Known<EquatableArray<Amount>>([new(Material, loose)]), ResourceConsumption = Known(new ResourceConsumption()),
    };

    [Fact]
    public void StartingWorkMovesInputsOutOfStockWithoutChargingTheIterationAgain()
    {
        var before = ResourceDemandOf(Facts(State(3, ""), 100), DefaultRoundsPolicy(), new());
        var started = ResourceDemandOf(Facts(State(3)), DefaultRoundsPolicy(), new());
        Assert.Equal(30, before.MaterialAvailability.Value[Material].Value);
        Assert.Equal(30, started.MaterialAvailability.Value[Material].Value);
        Assert.Equal(60, started.Needs[Material]);
        Assert.Equal(60, started.Retained.Value[Material]);
        Assert.True(MaterialBudgetCovers(started.MaterialAvailability.Value, [new(Material, 30)], 1));
        Assert.False(MaterialBudgetCovers(started.MaterialAvailability.Value, [new(Material, 31)], 1));
    }

    [Fact]
    public void PauseResumeAndCompletionRebuildFromCurrentBinding()
    {
        Assert.Empty(Promises(State(3) with { Suspended = Known(true) }).Value);
        Assert.Equal(20, Promises(State(3)).Value[0].MaterialClaims[0].Units.Value);
        // Completion decrements remaining AND clears the bound item. Both snapshots owe the same next two inputs.
        Assert.Equal(20, Promises(State(2, "")).Value[0].MaterialClaims[0].Units.Value);
        Assert.Equal(0, Promises(State(1)).Value[0].MaterialClaims[0].Units.Value);
        Assert.Empty(Promises(State(0, "")).Value);
    }

    [Fact]
    public void CancellationDoesNotInventRefundsOrReleaseAnAppliedNativeBill()
    {
        var cancelled = Batch with { Progress = Batch.Progress with { Stage = Stage.Cancelled } };
        Assert.Equal(20, Promises(State(3), cancelled).Value[0].MaterialClaims[0].Units.Value);
        // Destroying only the unfinished item restores the full remaining obligation; recovery is observed stock only.
        var lost = ResourceDemandOf(Facts(State(3, "")), DefaultRoundsPolicy(), new());
        Assert.Equal(20, lost.MaterialAvailability.Value[Material].Value);
        var removed = ProductionPromisesOf(Known<EquatableArray<ProductionBatch>>([cancelled]), Known(default(EquatableArray<ProductionBatchState>)));
        var demand = ResourceDemandOf(Facts(State(3)) with { ProductionPromises = removed }, DefaultRoundsPolicy(), new());
        Assert.Equal(50, demand.MaterialAvailability.Value[Material].Value);
    }

    [Fact]
    public void EmbeddedAlternativeFundsOneIterationOnlyAndDoesNotEnterFreeStock()
    {
        var state = State(3) with { UnfinishedWork = Known(new ProductionUnfinishedWork("unfinished", [new(Alternate, 99)])) };
        var claims = Promises(state).Value;
        Assert.Equal(20, claims[0].MaterialClaims[0].Units.Value);
        Assert.DoesNotContain(claims[0].MaterialClaims, c => c.Resource == Alternate);
        var ambiguous = Batch with
        {
            Order = new ProductionBill("bench", "TestRecipe", BillMode.GearBatch, 3),
            Slots = Known<EquatableArray<EquatableArray<Amount>>>([[new(Material, 10), new(Alternate, 5)]]),
        };
        Assert.All(Promises(state, ambiguous).Value[0].MaterialClaims, c => Assert.False(c.Units.IsKnown));
        Assert.All(Promises(state with { Remaining = Known(1) }, ambiguous).Value[0].MaterialClaims, c => Assert.Equal(0, c.Units.Value));
    }

    [Fact]
    public void NativeLooseHoldsOverlapFreshPromiseOnly()
    {
        var facts = Facts(State(3)) with { BillReservations = Known<EquatableArray<IngredientReservation>>([
            new() { Bill = Known("bill"), Pawn = new RimGovernor.Brain.Domain.PawnId("worker"), Items = [new(Material, 10)] }]) };
        Assert.Equal(30, ResourceDemandOf(facts, DefaultRoundsPolicy(), new()).MaterialAvailability.Value[Material].Value);
        var last = facts with { ProductionPromises = Promises(State(1)) };
        Assert.Equal(40, ResourceDemandOf(last, DefaultRoundsPolicy(), new()).MaterialAvailability.Value[Material].Value);
    }

    [Fact]
    public void UnknownBindingAndDuplicateOwnershipCannotGrantCredit()
    {
        Assert.False(Promises(State(3) with { UnfinishedWork = default }).Value[0].MaterialClaims[0].Units.IsKnown);
        var second = Batch with { Progress = Batch.Progress with { Action = new ActionId("second"), Bill = Known("second-bill") } };
        Assert.Throws<DomainException>(() => ProductionPromisesOf(Known<EquatableArray<ProductionBatch>>([Batch, second]),
            Known<EquatableArray<ProductionBatchState>>([State(3), State(3) with { Id = "second-bill" }])));
        var malformed = State(3) with { UnfinishedWork = Known(new ProductionUnfinishedWork("", [new(Material, 10)])) };
        Assert.Throws<DomainException>(() => Promises(malformed));
    }

    [Fact]
    public void WireRoundTripAndProjectionDistinguishAbsentUnreadAndEmbedded()
    {
        var bill = new O.BillState
        {
            Id = "bill", ManagedUnchanged = true, Suspended = false, Finished = false, RepeatMode = Ops.RepeatMode.Count, RepeatCount = 3,
            UnfinishedWork = new() { Item = "unfinished", Ingredients = [new() { DefName = Material.Value, Units = 10 }] },
        };
        ProductionBatchState Read(O.BillState row) => ConstructionReads.ProductionBatchStates(new() { Benches = [new() { Bills = [row] }] }).Value[0];
        var decoded = WireJson.Deserialize<O.BillState>(WireJson.Serialize(bill));
        Assert.Equal(20, Promises(Read(decoded)).Value[0].MaterialClaims[0].Units.Value);
        Assert.Equal(30, Promises(Read(bill with { UnfinishedWork = new() { Item = "" } })).Value[0].MaterialClaims[0].Units.Value);
        Assert.False(Read(bill with { UnfinishedWork = null }).UnfinishedWork.IsKnown);
        Assert.False(Read(bill with { UnfinishedWork = new() { Item = "" , Ingredients = bill.UnfinishedWork.Ingredients } }).UnfinishedWork.IsKnown);
        Assert.False(Read(bill with { UnfinishedWork = new() { Item = "unfinished", Ingredients = [new() { DefName = Material.Value, Units = null }] } }).UnfinishedWork.IsKnown);
    }
}
