using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class AdmissionStatesTests
{
    [Theory]
    [InlineData(false, ProductionPromiseBlock.BatchLimit)]
    [InlineData(true, ProductionPromiseBlock.FutureAction)]
    public void AnIncompleteJournalCensusCannotFundNewWork(bool future, ProductionPromiseBlock reason)
    {
        ProductionBatchCensus census = future ? new ProductionBatchCensus.AheadOfObservation() : new ProductionBatchCensus.LimitExceeded();
        var promises = ProductionRules.ProductionPromisesOf(census, [], []);
        Assert.Equal(reason, Assert.IsType<ProductionPromiseSet.Blocked>(promises).Reason);
        Assert.Equal(MaterialBudgetBlock.ProductionPromises, Assert.IsType<MaterialAvailability.Blocked>(
            ProductionRules.ProductionMaterialHoldsOf(promises, [])).Reason);
        Assert.False(ProductionRules.ProductionAdmissionFits(new(), census, []));
    }

    [Theory]
    [InlineData(ProductionPromiseBlock.AwaitingReview)]
    [InlineData(ProductionPromiseBlock.BatchLimit)]
    [InlineData(ProductionPromiseBlock.FutureAction)]
    [InlineData(ProductionPromiseBlock.UnpricedRecipe)]
    [InlineData(ProductionPromiseBlock.EditedBill)]
    [InlineData(ProductionPromiseBlock.NoIngredientAlternative)]
    [InlineData(ProductionPromiseBlock.TruncatedWork)]
    public void BlockedPromisesSurviveJournalCodec(ProductionPromiseBlock reason)
    {
        var evidence = new WorkAdmissionEvidence { Promises = new ProductionPromiseSet.Blocked(reason) };
        var encoded = RimGovernor.Brain.Store.Codec.Codecs.Default;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer)) encoded.Write(writer, evidence);
        using var document = System.Text.Json.JsonDocument.Parse(buffer.WrittenMemory);
        var back = encoded.Read<WorkAdmissionEvidence>(document.RootElement);
        Assert.Equal(reason, Assert.IsType<ProductionPromiseSet.Blocked>(back.Promises).Reason);
    }

    [Fact]
    public void AwaitingReviewAndUnpricedWorkNeverSupplyABudget()
    {
        var empty = new ProductionPromiseSet.Assessed([]);
        var noReview = ProductionRules.SharedMaterialBudgetOf([], null, ProductionRules.ProductionMaterialHoldsOf(empty, []));
        Assert.Equal(MaterialBudgetBlock.AwaitingReview, Assert.IsType<MaterialAvailability.Blocked>(noReview).Reason);
        var choice = ForecastMaterialChoices.CompareMaterialPlans([], noReview);
        Assert.Empty(choice.Selected);
        Assert.Contains("material_budget_blocked:AwaitingReview", choice.Findings);
        var view = WorkRules.WorkResourcesOf(new(), new ProductionBatchCensus.Read([]), [], truncated: true);
        Assert.Equal(MaterialBudgetBlock.WorkClaims, Assert.IsType<MaterialAvailability.Blocked>(view.Materials).Reason);
    }

    [Fact]
    public void MissingNativePawnClockFailsTheReviewBoundary()
    {
        var facts = WorkResourceTestFacts.Create("pawn") with { Census = (WorkResourceTestFacts.Create("pawn")).Census with { WorkResources = [] } };
        Assert.Throws<RequiredFactException>(() => ProductionRules.WorkAdmissionEvidenceOf(facts, new ProductionBatchCensus.Read([])));
    }
    [Theory]
    [InlineData(ProductionPromiseBlock.UnpricedRecipe)]
    [InlineData(ProductionPromiseBlock.EditedBill)]
    [InlineData(ProductionPromiseBlock.NoIngredientAlternative)]
    public void UnpriceableBatchesExplainWhyThePromiseSetIsBlocked(ProductionPromiseBlock reason)
    {
        var batch = new ProductionBatch(new("bench", "recipe", BillMode.GearBatch, 1, "Steel"),
            new ProgressView { Action = new("batch"), Stage = Stage.Pending });
        BatchPricing pricing = new BatchPricing.Priced(batch, [[new(new("Steel"), 2)]], false);
        EquatableArray<ProductionBatchState> bills = [];
        if (reason == ProductionPromiseBlock.UnpricedRecipe) pricing = new BatchPricing.Unpriced(batch);
        if (reason == ProductionPromiseBlock.NoIngredientAlternative)
            pricing = new BatchPricing.Priced(batch, [[new(new("WoodLog"), 2)]], false);
        if (reason == ProductionPromiseBlock.EditedBill)
        {
            batch = batch with { Progress = batch.Progress with { Attempt = new(1), Stage = Stage.Completed, Bill = "bill" } };
            pricing = new BatchPricing.Priced(batch, [[new(new("Steel"), 2)]], false);
            bills = [new(BillWire.Bill("bill") with { RepeatMode = RimGovernor.Contracts.Wire.Operations.RepeatMode.Count })];
        }
        Assert.Equal(reason, Assert.IsType<ProductionPromiseSet.Blocked>(ProductionRules.ProductionPromisesOf([pricing], bills)).Reason);
    }

    [Fact]
    public void TradeRetentionDistinguishesTheConsumptionReadFromAnOpenMaterialObligation()
    {
        var facts = WorkResourceTestFacts.Create();
        Assert.IsType<TradeRetention.AwaitingConsumption>(ResourceRules.ResourceDemandOf(facts, RoundsRules.DefaultRoundsPolicy(), new()).Retained);
        facts = facts with { ResourceConsumption = new ResourceConsumption() };
        Assert.IsType<TradeRetention.OpenMaterialObligation>(ResourceRules.ResourceDemandOf(facts, RoundsRules.DefaultRoundsPolicy(), new()).Retained);
        facts = facts with { ProductionPromises = new ProductionPromiseSet.Assessed([]) };
        Assert.IsType<TradeRetention.Assessed>(ResourceRules.ResourceDemandOf(facts, RoundsRules.DefaultRoundsPolicy(), new()).Retained);
    }
}
