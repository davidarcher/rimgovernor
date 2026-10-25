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

    private static EquatableArray<ProductionBatchState> Census(int remaining = 5) =>
        [
            new(
                "bill",
                true,
                BillRepeat.Count,
                remaining,
                false,
                false,
                new ProductionUnfinishedWork("", default)
            ),
        ];

    private static ProductionBatch Placed =>
        Batch with
        {
            Progress = Batch.Progress with
            {
                Attempt = new AttemptId(1),
                Stage = Stage.Completed,
                Bill = "bill",
            },
        };

    private static ProductionPromiseSet Promises(
        ProductionBatch batch,
        EquatableArray<ProductionBatchState> census = default
    ) => Promises(Priced(batch), census);

    private static ProductionPromiseSet Promises(
        BatchPricing batch,
        EquatableArray<ProductionBatchState> census = default
    ) => ProductionRules.ProductionPromisesOf([batch], census);

    private static EquatableArray<IngredientReservation> Reserved(string bill) =>
        [
            new()
            {
                Pawn = new RimGovernor.Brain.Domain.PawnId("cook"),
                Bill = bill,
                Items = [new(Material, 10)],
            },
        ];

    private static RoundsFacts Facts(ProductionPromiseSet promises) =>
        new()
        {
            MedicalReserve = new() { Catalog = ItemFactsFixtureRules.CoreItemFacts() }, Upkeep = new() { Rooms = RoomTestFacts.NoRooms, Routes = new() },
            Comfort = ComfortTests.NoComfortObservation,
            BasicComfort = ComfortTests.NoComfortObservation,
            ProductionPromises = promises,
            BillReservations = EmptyReservations,
            Resources = [new(Material, 100), new(Other, 20)],
            Gear = ColonyModelFixtures.NoGear(), CurrentConstruction = CurrentConstruction.Empty with
            {
                Deficit = new Dictionary<Resource, long> { [Material] = 60 },
            },
            ResourceConsumption = new ResourceConsumption(),
        };

    [Fact]
    public void PendingBatchAndConstructionAddInsteadOfCompetingStockFloors()
    {
        var facts = Facts(Promises(Batch));
        var demand = ResourceRules.ResourceDemandOf(facts, RoundsRules.DefaultRoundsPolicy(), new());
        Assert.Equal(110, demand.Needs[Material]);
        Assert.Equal(110, demand.Retained.RetainedStock()[Material]);
        Assert.Equal(-10, demand.MaterialAvailability.AssessedBalances()[Material].ExactCount());
        Assert.False(ProductionRules.MaterialBudgetCovers(demand.MaterialAvailability.AssessedBalances(), [new(Material, 5)], 1));
        Assert.True(ProductionRules.MaterialBudgetCovers(demand.MaterialAvailability.AssessedBalances(), [new(Other, 5)], 4));
        Assert.Equal(20, demand.MaterialAvailability.AssessedBalances()[Other].ExactCount());
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
        Assert.Equal(-10, demand.MaterialAvailability.AssessedBalances()[Material].ExactCount());
        var remaining = ResourceRules.ResourceDemandOf(facts with { ProductionPromises = Promises(Placed, Census(2)) }, RoundsRules.DefaultRoundsPolicy(), new());
        Assert.Equal(80, remaining.Needs[Material]);
        Assert.Equal(20, remaining.MaterialAvailability.AssessedBalances()[Material].ExactCount());
        var unrelated = ResourceRules.ResourceDemandOf(facts with { BillReservations = Reserved("another-bill") }, RoundsRules.DefaultRoundsPolicy(), new());
        Assert.Equal(120, unrelated.Needs[Material]);
    }

    [Fact]
    public void CancellationReleasesPendingButAppliedBillRequiresNativeEvidence()
    {
        Assert.Empty(
            Promises(
                Batch with
                {
                    Progress = Batch.Progress with { Stage = Stage.Cancelled },
                }
            ).AssessedPromises()
        );
        var unsent = Batch with
        {
            Progress = Batch.Progress with
            {
                Attempt = new AttemptId(1),
                Effect = new EffectState.Observed(Effect.Absent),
            },
        };
        Assert.Equal(50, Promises(unsent).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.Empty(Promises(unsent with { Progress = unsent.Progress with { Stage = Stage.Cancelled } }).AssessedPromises());
        var cancelled = Placed with { Progress = Placed.Progress with { Stage = Stage.Cancelled } };
        Assert.Equal(50, Promises(cancelled, Census()).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.Empty(Promises(cancelled, default(EquatableArray<ProductionBatchState>)).AssessedPromises());
        Assert.Empty(Promises(Placed, Census(0)).AssessedPromises());
        EquatableArray<ProductionBatchState> suspended = [Census()[0] with { Suspended = true }];
        Assert.Empty(Promises(Placed, suspended).AssessedPromises());
        // A lingering native job still owns its materials after the future promise ends.
        Assert.Equal(10, ProductionRules.ProductionMaterialHoldsOf(Promises(Placed, Census(0)), Reserved("bill")).AssessedBalances()[Material].ExactCount());
    }

    [Fact]
    public void UnknownReadbackIsNotCompletionAndUnknownChoicesAreResourceLocal()
    {
        var unlinked = Placed with { Progress = Placed.Progress with { Bill = null } };
        var unread = ResourceRules.ResourceDemandOf(Facts(Promises(unlinked)), RoundsRules.DefaultRoundsPolicy(), new());
        Assert.IsType<MaterialUnits.Open>(unread.MaterialAvailability.AssessedBalances()[Material]);
        Assert.False(ProductionRules.MaterialBudgetCovers(unread.MaterialAvailability.AssessedBalances(), [new(Material, 1)], 1));
        Assert.Equal(20, unread.MaterialAvailability.AssessedBalances()[Other].ExactCount());
        Assert.IsType<TradeRetention.OpenMaterialObligation>(unread.Retained);
        var ambiguous = Priced(Batch with { Order = new ProductionBill("bench", "TestRecipe", BillMode.GearBatch, 5) },
            [[new(Material, 10), new(Other, 6)]]);
        Assert.All(Promises(ambiguous).AssessedPromises()[0].MaterialClaims, c => Assert.IsType<MaterialUnits.Open>(c.Units));
        // A batch the catalog does not price leaves every promise unread, even beside a priced one.
        Assert.Equal(ProductionPromiseBlock.UnpricedRecipe, Assert.IsType<ProductionPromiseSet.Blocked>(Promises(new BatchPricing.Unpriced(Batch))).Reason);
        var second = Batch with { Progress = Batch.Progress with { Action = new ActionId("second") } };
        Assert.IsType<ProductionPromiseSet.Blocked>(ProductionRules.ProductionPromisesOf([Priced(Batch), new BatchPricing.Unpriced(second)], default));
        Assert.Equal(50, Promises(Priced(Batch, unfinished: true)).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.Empty(ProductionRules.ProductionPromisesOf([], Census()).AssessedPromises());
        Assert.IsType<MaterialAvailability.Blocked>(ResourceRules.ResourceDemandOf(Facts(new ProductionPromiseSet.Blocked(ProductionPromiseBlock.TruncatedWork)), RoundsRules.DefaultRoundsPolicy(), new()).MaterialAvailability);
    }

    [Fact]
    public void RepeatedSlotsSumAndIndependentMethodsCompete()
    {
        var batch = Priced(Batch, [[new(Material, 10)], [new(Material, 2)]]);
        Assert.Equal(60, Promises(batch).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        var second = Batch with { Progress = Batch.Progress with { Action = new ActionId("second") } };
        var promises = ProductionRules.ProductionPromisesOf([Priced(Batch), Priced(second)], default);
        Assert.Equal(100, ProductionRules.ProductionMaterialHoldsOf(promises, EmptyReservations).AssessedBalances()[Material].ExactCount());
        Assert.Throws<DomainException>(() => ProductionRules.ProductionPromisesOf([Priced(Placed), Priced(Placed)], Census()));
    }

    [Fact]
    public void IndefiniteStockTargetIsNotPretendedToBeAnExactBatch()
    {
        var standing = Batch with { Order = new ProductionBill("bench", "TestRecipe", BillMode.StockTarget, 5) };
        Assert.Empty(Promises(standing).AssessedPromises());
    }

    [Fact]
    public void NativeProjectionCountsOnlyACountBillsRemaining()
    {
        var bill = BillWire.Bill("bill") with { ManagedUnchanged = true, Finished = false, RepeatMode = Ops.RepeatMode.Count, RepeatCount = 3 };
        O.BillsSnapshot Snapshot(O.BillState row) => BillWire.Census(row);
        var census = ConstructionReads.ProductionBatchStates(Snapshot(bill));
        Assert.Equal(30, Promises(Placed, census).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.IsType<MaterialUnits.Open>(Promises(Placed, ConstructionReads.ProductionBatchStates(Snapshot(bill with { RepeatMode = Ops.RepeatMode.Target }))).AssessedPromises()[0].MaterialClaims[0].Units);
        Assert.Throws<DomainException>(() => ConstructionReads.ProductionBatchStates(Snapshot(bill with { RepeatMode = (Ops.RepeatMode)0 })));
        Assert.Throws<RequiredFactException>(() => ConstructionReads.ProductionBatchStates(null));
        Assert.Throws<RequiredFactException>(() => ConstructionReads.ProductionBatchStates(Snapshot(bill with { Id = "" })));
    }

    [Fact]
    public void CommittedMethodPublishesBatchThroughTheJournalAndScopesItToItsWorld()
    {
        using var lifetime = new CancellationTokenSource();
        using var journal = JournalStore.Open(Path.Combine(Path.GetTempPath(), "rg-production-" + Guid.NewGuid().ToString("N") + ".db"), lifetime.Token);
        var world = new GenerationSnapshot(new ColonyId("colony"), new MapId(1), new LoadId("load"), new PlanId("review"), new PlanRevision(1), default);
        var facts = Facts(new ProductionPromiseSet.Assessed([])) with
        {
            Tick = new Tick(100), Resources = [new(Material, 120)],
            ForecastObserved = new() { WorkPawns = new WorkPawnsRead(default(EquatableArray<WorkPawn>)) },
            WorkResources = default(EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>),
            WorkAdmission = new() { Recipes = [new("TestRecipe", Slots, false) { Products = [], WorkAmount = 0 }],
                Bills = default(EquatableArray<ProductionBatchState>) },
        };
        var review = journal.ReviewRounds(new RoundsRequest { Current = world, Tick = facts.Tick, Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, lifetime.Token);
        Assert.True(review.Review.Enabled);
        Assert.Empty(journal.ProductionBatches(world, facts.Tick, lifetime.Token).ReadBatches());
        var action = DomainAction.NewProductionBillAction(new ActionId("batch"), Batch.Order);
        var plan = new PlanSpec(new PlanId("method-plan"), new PlanRevision(1), [action]);
        journal.CommitClaimedMethod(AdminFamily.WorkLedger, world, new MethodId("batch-method"), "test", plan, lifetime.Token);
        var committed = Assert.Single(journal.ProductionBatches(world, facts.Tick, lifetime.Token).ReadBatches());
        Assert.Equal(action.Id, committed.Progress.Action);
        Assert.Equal(Batch.Order, committed.Order);
        Assert.Equal(50, Promises(committed).AssessedPromises()[0].MaterialClaims[0].ExactCount());
        Assert.Empty(journal.ProductionBatches(world with { Load = new LoadId("different-load") }, facts.Tick, lifetime.Token).ReadBatches());
        journal.Cancel(plan.Id, action.Id, lifetime.Token);
        var cancelled = Assert.Single(journal.ProductionBatches(world, facts.Tick, lifetime.Token).ReadBatches());
        Assert.Empty(Promises(cancelled).AssessedPromises());
    }
}
