using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ProductionNativeOwnershipTests
{
    [Fact]
    public void NativeNoneAndBoundStatesFeedTheSameSharedMaterialBudget()
    {
        var steel = new Resource("Steel");
        var batch = new ProductionBatch(new ProductionBill("bench", "recipe", BillMode.GearBatch, 3, steel.Value),
            new ProgressView { Action = new ActionId("action"), Stage = Stage.Completed, Attempt = new AttemptId(1), Bill = "bill" });
        var pricing = new BatchPricing.Priced(batch, [[new(steel, 10)]], true);
        var bill = BillWire.Bill("bill") with
        {
            ManagedUnchanged = true, RepeatMode = RimGovernor.Contracts.Wire.Operations.RepeatMode.Count, RepeatCount = 3,
            Reservations = [new() { PawnId = "worker", Items = [new() { DefName = steel.Value, Units = 10 }] }],
        };
        double Free(O.BillState row, long stock)
        {
            var census = BillWire.Census(row);
            var promises = ProductionRules.ProductionPromisesOf([pricing], ConstructionReads.ProductionBatchStates(census));
            return ForecastClaims.ForecastMaterialBudgetOf([new(steel, stock)], ConstructionReads.BillReservations(census), promises.AssessedPromises(), 0)[steel].ExactCount();
        }
        Assert.Equal(70, Free(bill, 100));
        var bound = new O.BillUnfinishedWork.Bound { Item = "unfinished", Ingredients = [new() { DefName = steel.Value, Units = 10 }] };
        Assert.Equal(70, Free(bill with { UnfinishedWork = bound }, 90));
        Assert.Throws<DomainException>(() => ProductionRules.ProductionPromisesOf([pricing],
            ConstructionReads.ProductionBatchStates(BillWire.Census(bill, bill))));
    }

    [Fact]
    public void BillAndReservationJoinsRetainActualRows()
    {
        var reservation = new O.IngredientReservation { PawnId = "worker", Items = [new() { DefName = "Steel", Units = 10 }] };
        var bound = new O.BillUnfinishedWork.Bound { Item = "unfinished", Ingredients = [new() { DefName = "Steel", Units = 10 }] };
        var bill = BillWire.Bill("bill") with { UnfinishedWork = bound, Reservations = [reservation] };
        var census = BillWire.Census(bill);
        var state = Assert.Single(ConstructionReads.ProductionBatchStates(census));
        Assert.Same(bill, state.Observed);
        Assert.Same(bound, state.UnfinishedWork);
        Assert.Same(bound.Ingredients[0], Assert.IsType<O.BillUnfinishedWork.Bound>(state.UnfinishedWork).Ingredients[0]);
        var join = Assert.Single(ConstructionReads.BillReservations(census));
        Assert.Same(reservation, join.Observed);
        Assert.Equal("bill", join.Bill);
        Assert.Equal(new PawnId("worker"), join.Pawn);
        Assert.Equal(new Amount(new Resource("Steel"), 10), Assert.Single(join.Items));
        var none = new O.BillUnfinishedWork.None();
        Assert.Same(none, Assert.Single(ConstructionReads.ProductionBatchStates(BillWire.Census(bill with { UnfinishedWork = none }))).UnfinishedWork);
    }

    [Fact]
    public void InvalidNativeIdentitiesAndDuplicateEmbeddedDefinitionsStillFailTheRead()
    {
        var reservation = new O.IngredientReservation { PawnId = "worker", Items = [new() { DefName = "Steel", Units = 0 }] };
        var bill = BillWire.Bill("bill") with { Reservations = [reservation] };
        Assert.Equal(0, Assert.Single(Assert.Single(ConstructionReads.BillReservations(BillWire.Census(bill))).Items).Count);
        Assert.Throws<RequiredFactException>(() => ConstructionReads.BillReservations(BillWire.Census(bill with { Reservations = [reservation with { PawnId = " " }] })));
        Assert.Throws<RequiredFactException>(() => ConstructionReads.BillReservations(BillWire.Census(bill with { Reservations = [reservation with { Items = [new() { DefName = "Steel", Units = -1 }] }] })));
        var duplicate = new O.BillUnfinishedWork.Bound
        { Item = "unfinished", Ingredients = [new() { DefName = "Steel", Units = 1 }, new() { DefName = "Steel", Units = 2 }] };
        Assert.Throws<RequiredFactException>(() => ConstructionReads.ProductionBatchStates(BillWire.Census(bill with { UnfinishedWork = duplicate })));
    }

    [Fact]
    public void ReservationJournalPayloadKeepsItsExactFlatShapeAndReconstructsTheWholeNativeRow()
    {
        const string payload = "{\"Bill\":\"bill\",\"Pawn\":\"worker\",\"Items\":[{\"Resource\":\"Steel\",\"Count\":10}]}";
        var join = Codecs.Default.Deserialize<IngredientReservation>(payload);
        Assert.Equal("bill", join.Bill);
        Assert.Equal("worker", join.Observed.PawnId);
        Assert.Equal(new O.Quantity { DefName = "Steel", Units = 10 }, Assert.Single(join.Observed.Items));
        Assert.Equal(payload, Codecs.Default.Serialize(join));
        Assert.Equal(payload, Codecs.Default.Serialize(BillWire.Reservation("bill", "worker", new Amount(new Resource("Steel"), 10))));
    }
}
