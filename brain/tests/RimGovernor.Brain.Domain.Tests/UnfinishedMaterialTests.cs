using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class UnfinishedMaterialTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static readonly Resource Alternate = new("TestAlternate");
    private static ProductionBatch Batch =>
        new(
            new ProductionBill("bench", "TestRecipe", BillMode.GearBatch, 3, Material.Value),
            new ProgressView
            {
                Action = new ActionId("batch"),
                Stage = Stage.Completed,
                Attempt = new AttemptId(1),
                Bill = "bill",
            }
        );

    private static BatchPricing.Priced Priced(
        ProductionBatch batch,
        EquatableArray<EquatableArray<Amount>>? slots = null
    ) =>
        new BatchPricing.Priced(
            batch,
            slots
                ??
                [
                    [new(Material, 10)],
                ],
            true
        );

    private static ProductionBatchState State(int remaining, string item = "unfinished") =>
        new(BillWire.Bill("bill") with { ManagedUnchanged = true, RepeatMode = RimGovernor.Brain.GameFacts.Operations.RepeatMode.Count, RepeatCount = remaining,
            UnfinishedWork = item.Length == 0 ? new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.None() : new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.Bound { Item = item, Ingredients = [new() { DefName = Material.Value, Units = 10 }] } });

    private static ProductionPromiseSet Promises(
        ProductionBatchState state,
        BatchPricing? batch = null
    ) =>
        ProductionRules.ProductionPromisesOf(
            [batch ?? Priced(Batch)],
            [state]
        );

    private static ReviewFacts Facts(ProductionBatchState state, long loose = 90) =>
        new()
        {
            ProductionPromises = Promises(state),
            BillReservations = [],
            Gear = ColonyModelFixtures.NoGear(), CurrentConstruction = CurrentConstruction.Empty with
            {
                Deficit = new Dictionary<Resource, long> { [Material] = 40 },
            },
            Census = ColonyFactsCoreTests.Snapshot() with { Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = Material.Value, Units = loose }] },
            ResourceConsumption = new ResourceConsumption(),
        };

    [Fact]
    public void StartingWorkMovesInputsOutOfStockWithoutChargingTheIterationAgain()
    {
        var before = ResourceRules.ResourceDemandOf(Facts(State(3, ""), 100), Rules.DefaultPlannerSettings(), new());
        var started = ResourceRules.ResourceDemandOf(Facts(State(3)), Rules.DefaultPlannerSettings(), new());
        Assert.Equal(30, before.MaterialAvailability.AssessedBalances()[Material].ExactCount());
        Assert.Equal(30, started.MaterialAvailability.AssessedBalances()[Material].ExactCount());
        Assert.Equal(60, started.Needs[Material]);
        Assert.Equal(60, started.Retained.RetainedStock()[Material]);
        Assert.True(ProductionRules.MaterialBudgetCovers(started.MaterialAvailability.AssessedBalances(), [new(Material, 30)], 1));
        Assert.False(ProductionRules.MaterialBudgetCovers(started.MaterialAvailability.AssessedBalances(), [new(Material, 31)], 1));
    }

    [Fact]
    public void PauseResumeAndCompletionRebuildFromCurrentBinding()
    {
        Assert.Empty(Promises(new ProductionBatchState(State(3).Observed with { Suspended = true })).AssessedPromises());
        Assert.Equal(20, Promises(State(3)).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        // Completion decrements remaining AND clears the bound item. Both snapshots owe the same next two inputs.
        Assert.Equal(20, Promises(State(2, "")).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.Equal(0, Promises(State(1)).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.Empty(Promises(State(0, "")).AssessedPromises());
    }

    [Fact]
    public void CancellationDoesNotInventRefundsOrReleaseAnAppliedNativeBill()
    {
        var cancelled = Batch with { Progress = Batch.Progress with { Stage = Stage.Cancelled } };
        Assert.Equal(20, Promises(State(3), Priced(cancelled)).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        // Destroying only the unfinished item restores the full remaining obligation; recovery is observed stock only.
        var lost = ResourceRules.ResourceDemandOf(Facts(State(3, "")), Rules.DefaultPlannerSettings(), new());
        Assert.Equal(20, lost.MaterialAvailability.AssessedBalances()[Material].ExactCount());
        var removed = ProductionRules.ProductionPromisesOf([Priced(cancelled)], default(EquatableArray<ProductionBatchState>));
        var demand = ResourceRules.ResourceDemandOf(Facts(State(3)) with { ProductionPromises = removed }, Rules.DefaultPlannerSettings(), new());
        Assert.Equal(50, demand.MaterialAvailability.AssessedBalances()[Material].ExactCount());
    }

    [Fact]
    public void EmbeddedAlternativeFundsOneIterationOnlyAndDoesNotEnterFreeStock()
    {
        var state = new ProductionBatchState(State(3).Observed with { UnfinishedWork = new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.Bound { Item = "unfinished", Ingredients = [new() { DefName = Alternate.Value, Units = 99 }] } });
        var claims = Promises(state).AssessedPromises();
        Assert.Equal(20, claims[0].MaterialClaims[0].ExactCount());
        Assert.DoesNotContain(claims[0].MaterialClaims, c => c.Resource == Alternate);
        var ambiguous = Priced(Batch with { Order = new ProductionBill("bench", "TestRecipe", BillMode.GearBatch, 3) },
            [[new(Material, 10), new(Alternate, 5)]]);
        Assert.All(Promises(state, ambiguous).AssessedPromises()[0].MaterialClaims, c => Assert.IsType<MaterialUnits.Open>(c.Units));
        Assert.All(Promises(new ProductionBatchState(state.Observed with { RepeatCount = 1 }), ambiguous).AssessedPromises()[0].MaterialClaims, c => Assert.Equal(0, c.ExactCount()));
    }

    [Fact]
    public void NativeLooseHoldsOverlapFreshPromiseOnly()
    {
        var facts = Facts(State(3)) with { BillReservations = [
            BillWire.Reservation("bill", "worker", new Amount(Material, 10))] };
        Assert.Equal(30, ResourceRules.ResourceDemandOf(facts, Rules.DefaultPlannerSettings(), new()).MaterialAvailability.AssessedBalances()[Material].ExactCount());
        var last = facts with { ProductionPromises = Promises(State(1)) };
        Assert.Equal(40, ResourceRules.ResourceDemandOf(last, Rules.DefaultPlannerSettings(), new()).MaterialAvailability.AssessedBalances()[Material].ExactCount());
    }

    [Fact]
    public void DuplicateOwnershipAndMalformedBindingThrow()
    {
        var second = Batch with
        {
            Progress = Batch.Progress with
            {
                Action = new ActionId("second"),
                Bill = "second-bill",
            },
        };
        Assert.Throws<DomainException>(() =>
            ProductionRules.ProductionPromisesOf(
                [Priced(Batch), Priced(second)],
                [
                    State(3),
                    new(State(3).Observed with { Id = "second-bill" }),
                ]
            )
        );
        var malformed = new ProductionBatchState(State(3).Observed with
        { UnfinishedWork = new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.Bound { Item = "", Ingredients = [new() { DefName = Material.Value, Units = 10 }] } });
        Assert.Throws<DomainException>(() => Promises(malformed));
    }

    [Fact]
    public void WireRoundTripAndProjectionDistinguishAbsentAndEmbedded()
    {
        var bill = BillWire.Bill("bill") with
        {
            ManagedUnchanged = true, Finished = false, RepeatMode = RimGovernor.Brain.GameFacts.Operations.RepeatMode.Count, RepeatCount = 3,
            UnfinishedWork = new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.Bound { Item = "unfinished", Ingredients = [new() { DefName = Material.Value, Units = 10 }] },
        };
        ProductionBatchState Read(RimGovernor.Brain.GameFacts.Observations.BillState row) => ConstructionReads.ProductionBatchStates(BillWire.Census(row))[0];
        var decoded = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.BillState>(OrdersJson.Serialize(bill));
        Assert.Equal(20, Promises(Read(decoded)).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.Equal(30, Promises(Read(bill with { UnfinishedWork = new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.None() })).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.Throws<RequiredFactException>(() => Read(bill with { UnfinishedWork = new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.Bound { Item = "", Ingredients = [] } }));
        Assert.Throws<RequiredFactException>(() => Read(bill with { UnfinishedWork = new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.Bound { Item = "unfinished", Ingredients = [new() { DefName = Material.Value, Units = 0 }] } }));
    }
}
