using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Observation;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;
using Ops = RimGovernor.Contracts.Wire.Operations;
using RimGovernor.Brain.Store;
using JournalStore = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ProductionPromisesTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static readonly Resource Other = new("TestOther");
    private static ProductionBatch Batch => new(new ProductionBill("bench", "TestRecipe", BillMode.GearBatch, 5, Material.Value),
        new ProgressView { Action = new ActionId("batch"), Stage = Stage.Pending });
    private static readonly EquatableArray<EquatableArray<Amount>> Slots = [[new(Material, 10)]];
    private static BatchPricing.Priced Priced(ProductionBatch batch, EquatableArray<EquatableArray<Amount>>? slots = null, bool unfinished = false) =>
        new BatchPricing.Priced(batch, slots ?? Slots, unfinished);
    private static EquatableArray<IngredientReservation> EmptyReservations => [];
    private static Fact<EquatableArray<ProductionBatchState>> Census(int remaining = 5) => Known<EquatableArray<ProductionBatchState>>([
        new("bill", true, BillRepeat.Count, remaining, false, false, new ProductionUnfinishedWork("", default))]);
    private static ProductionBatch Placed => Batch with
    { Progress = Batch.Progress with { Attempt = new AttemptId(1), Stage = Stage.Completed, Bill = Known("bill") } };
    private static Fact<EquatableArray<ForecastCommitment>> Promises(ProductionBatch batch, Fact<EquatableArray<ProductionBatchState>> census = default) =>
        Promises(Priced(batch), census);
    private static Fact<EquatableArray<ForecastCommitment>> Promises(BatchPricing batch, Fact<EquatableArray<ProductionBatchState>> census = default) =>
        ProductionRules.ProductionPromisesOf(Known<EquatableArray<BatchPricing>>([batch]), census);
    private static EquatableArray<IngredientReservation> Reserved(string bill) => [
        new() { Pawn = new RimGovernor.Brain.Domain.PawnId("cook"), Bill = bill, Items = [new(Material, 10)] }];
    private static RoundsFacts Facts(Fact<EquatableArray<ForecastCommitment>> promises) => new()
    {
        ProductionPromises = promises, BillReservations = EmptyReservations,
        Resources = Known<EquatableArray<Amount>>([new(Material, 100), new(Other, 20)]),
        CurrentConstruction = CurrentConstruction.Empty with { Deficit = new Dictionary<Resource, long> { [Material] = 60 } },
        ResourceConsumption = Known(new ResourceConsumption()),
    };

    [Fact]
    public void PendingBatchAndConstructionAddInsteadOfCompetingStockFloors()
    {
        var facts = Facts(Promises(Batch));
        var demand = ResourceRules.ResourceDemandOf(facts, RoundsRules.DefaultRoundsPolicy(), new());
        Assert.Equal(110, demand.Needs[Material]);
        Assert.Equal(110, demand.Retained.Value[Material]);
        Assert.Equal(-10, demand.MaterialAvailability.Value[Material].Value);
        Assert.False(ProductionRules.MaterialBudgetCovers(demand.MaterialAvailability.Value, [new(Material, 5)], 1));
        Assert.True(ProductionRules.MaterialBudgetCovers(demand.MaterialAvailability.Value, [new(Other, 5)], 4));
        Assert.Equal(20, demand.MaterialAvailability.Value[Other].Value);
        Assert.Equal(facts.ProductionPromises, demand.Work.ProductionPromises);
        // Changing the output's name does not change ingredient contention; this applies to a cooking batch too.
        var cooking = Batch with { Order = new ProductionBill("stove", "TestCookingRecipe", BillMode.GearBatch, 5, Material.Value) };
        Assert.Equal(demand.Needs[Material], ResourceRules.ResourceDemandOf(Facts(Promises(cooking)), RoundsRules.DefaultRoundsPolicy(), new()).Needs[Material]);
    }

    [Fact]
    public void ExactNativeHandoffCountsOnceAndCompletedIterationsReduceThePromise()
    {
        var facts = Facts(Promises(Placed, Census())) with { BillReservations = Reserved("bill") };
        var demand = ResourceRules.ResourceDemandOf(facts, RoundsRules.DefaultRoundsPolicy(), new());
        Assert.Equal(110, demand.Needs[Material]);
        Assert.Equal(-10, demand.MaterialAvailability.Value[Material].Value);
        var remaining = ResourceRules.ResourceDemandOf(facts with { ProductionPromises = Promises(Placed, Census(2)) }, RoundsRules.DefaultRoundsPolicy(), new());
        Assert.Equal(80, remaining.Needs[Material]);
        Assert.Equal(20, remaining.MaterialAvailability.Value[Material].Value);
        var unrelated = ResourceRules.ResourceDemandOf(facts with { BillReservations = Reserved("another-bill") }, RoundsRules.DefaultRoundsPolicy(), new());
        Assert.Equal(120, unrelated.Needs[Material]);
    }

    [Fact]
    public void CancellationReleasesPendingButAppliedBillRequiresNativeEvidence()
    {
        Assert.Empty(Promises(Batch with { Progress = Batch.Progress with { Stage = Stage.Cancelled } }).Value);
        var unsent = Batch with { Progress = Batch.Progress with { Attempt = new AttemptId(1), Effect = Known(Effect.Absent) } };
        Assert.Equal(50, Promises(unsent).Value[0].MaterialClaims[0].Units.Value);
        Assert.Empty(Promises(unsent with { Progress = unsent.Progress with { Stage = Stage.Cancelled } }).Value);
        var cancelled = Placed with { Progress = Placed.Progress with { Stage = Stage.Cancelled } };
        Assert.Equal(50, Promises(cancelled, Census()).Value[0].MaterialClaims[0].Units.Value);
        Assert.Empty(Promises(cancelled, Known(default(EquatableArray<ProductionBatchState>))).Value);
        Assert.Empty(Promises(Placed, Census(0)).Value);
        var suspended = Known<EquatableArray<ProductionBatchState>>([Census().Value[0] with { Suspended = true }]);
        Assert.Empty(Promises(Placed, suspended).Value);
        // A lingering native job still owns its materials after the future promise ends.
        Assert.Equal(10, ProductionRules.ProductionMaterialHoldsOf(Promises(Placed, Census(0)), Reserved("bill")).Value[Material].Value);
    }

    [Fact]
    public void UnknownReadbackIsNotCompletionAndUnknownChoicesAreResourceLocal()
    {
        var unread = ResourceRules.ResourceDemandOf(Facts(Promises(Placed)), RoundsRules.DefaultRoundsPolicy(), new());
        Assert.False(unread.MaterialAvailability.Value[Material].IsKnown);
        Assert.False(ProductionRules.MaterialBudgetCovers(unread.MaterialAvailability.Value, [new(Material, 1)], 1));
        Assert.Equal(20, unread.MaterialAvailability.Value[Other].Value);
        Assert.False(unread.Retained.IsKnown);
        var ambiguous = Priced(Batch with { Order = new ProductionBill("bench", "TestRecipe", BillMode.GearBatch, 5) },
            [[new(Material, 10), new(Other, 6)]]);
        Assert.All(Promises(ambiguous).Value[0].MaterialClaims, c => Assert.False(c.Units.IsKnown));
        // A batch the catalog does not price leaves every promise unread, even beside a priced one.
        Assert.False(Promises(new BatchPricing.Unpriced(Batch)).IsKnown);
        var second = Batch with { Progress = Batch.Progress with { Action = new ActionId("second") } };
        Assert.False(ProductionRules.ProductionPromisesOf(Known<EquatableArray<BatchPricing>>([Priced(Batch), new BatchPricing.Unpriced(second)]), default).IsKnown);
        Assert.Equal(50, Promises(Priced(Batch, unfinished: true)).Value[0].MaterialClaims[0].Units.Value);
        Assert.False(ProductionRules.ProductionPromisesOf(default, Census()).IsKnown);
        Assert.False(ResourceRules.ResourceDemandOf(Facts(default), RoundsRules.DefaultRoundsPolicy(), new()).MaterialAvailability.IsKnown);
    }

    [Fact]
    public void RepeatedSlotsSumAndIndependentMethodsCompete()
    {
        var batch = Priced(Batch, [[new(Material, 10)], [new(Material, 2)]]);
        Assert.Equal(60, Promises(batch).Value[0].MaterialClaims[0].Units.Value);
        var second = Batch with { Progress = Batch.Progress with { Action = new ActionId("second") } };
        var promises = ProductionRules.ProductionPromisesOf(Known<EquatableArray<BatchPricing>>([Priced(Batch), Priced(second)]), default);
        Assert.Equal(100, ProductionRules.ProductionMaterialHoldsOf(promises, EmptyReservations).Value[Material].Value);
        Assert.Throws<DomainException>(() => ProductionRules.ProductionPromisesOf(Known<EquatableArray<BatchPricing>>([Priced(Placed), Priced(Placed)]), Census()));
    }

    [Fact]
    public void IndefiniteStockTargetIsNotPretendedToBeAnExactBatch()
    {
        var standing = Batch with { Order = new ProductionBill("bench", "TestRecipe", BillMode.StockTarget, 5) };
        Assert.Empty(Promises(standing).Value);
    }

    [Fact]
    public void NativeProjectionCountsOnlyACountBillsRemaining()
    {
        var bill = BillWire.Bill("bill") with { ManagedUnchanged = true, Finished = false, RepeatMode = Ops.RepeatMode.Count, RepeatCount = 3 };
        O.BillsSnapshot Snapshot(O.BillState row) => BillWire.Census(row);
        var census = ConstructionReads.ProductionBatchStates(Snapshot(bill));
        Assert.Equal(30, Promises(Placed, census).Value[0].MaterialClaims[0].Units.Value);
        Assert.False(Promises(Placed, ConstructionReads.ProductionBatchStates(Snapshot(bill with { RepeatMode = Ops.RepeatMode.Target }))).Value[0].MaterialClaims[0].Units.IsKnown);
        Assert.Throws<RequiredFactException>(() => ConstructionReads.ProductionBatchStates(Snapshot(bill with { RepeatMode = Ops.RepeatMode.Unspecified })));
        Assert.False(ConstructionReads.ProductionBatchStates(null).IsKnown);
        Assert.False(ConstructionReads.ProductionBatchStates(Snapshot(bill with { Id = "" })).IsKnown);
    }

    [Fact]
    public void CommittedMethodPublishesBatchThroughTheJournalAndScopesItToItsWorld()
    {
        using var lifetime = new CancellationTokenSource();
        using var journal = JournalStore.Open(Path.Combine(Path.GetTempPath(), "rg-production-" + Guid.NewGuid().ToString("N") + ".db"), lifetime.Token);
        var world = new GenerationSnapshot(new ColonyId("colony"), new MapId(1), new LoadId("load"), new PlanId("review"), new PlanRevision(1), default);
        var facts = Facts(Known(default(EquatableArray<ForecastCommitment>))) with
        {
            Tick = new Tick(100), Resources = Known<EquatableArray<Amount>>([new(Material, 120)]),
            ForecastObserved = new() { WorkPawns = Known(default(EquatableArray<WorkPawn>)) },
            WorkResources = Known(default(EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>)),
            WorkAdmission = new() { Recipes = Known<EquatableArray<ProductionRecipeInput>>([new("TestRecipe", Slots, false) { Products = [], WorkAmount = 0 }]),
                Bills = Known(default(EquatableArray<ProductionBatchState>)) },
        };
        var review = journal.ReviewRounds(new RoundsRequest { Current = world, Tick = facts.Tick, Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, lifetime.Token);
        Assert.True(review.Review.Enabled);
        Assert.Empty(journal.ProductionBatches(world, facts.Tick, lifetime.Token).Value);
        var action = DomainAction.NewProductionBillAction(new ActionId("batch"), Batch.Order);
        var plan = new PlanSpec(new PlanId("method-plan"), new PlanRevision(1), [action]);
        journal.CommitClaimedMethod(AdminFamily.WorkLedger, world, new MethodId("batch-method"), "test", plan, lifetime.Token);
        var committed = Assert.Single(journal.ProductionBatches(world, facts.Tick, lifetime.Token).Value);
        Assert.Equal(action.Id, committed.Progress.Action);
        Assert.Equal(Batch.Order, committed.Order);
        Assert.Equal(50, Promises(committed).Value[0].MaterialClaims[0].Units.Value);
        Assert.Empty(journal.ProductionBatches(world with { Load = new LoadId("different-load") }, facts.Tick, lifetime.Token).Value);
        journal.Cancel(plan.Id, action.Id, lifetime.Token);
        var cancelled = Assert.Single(journal.ProductionBatches(world, facts.Tick, lifetime.Token).Value);
        Assert.Empty(Promises(cancelled).Value);
    }
}
