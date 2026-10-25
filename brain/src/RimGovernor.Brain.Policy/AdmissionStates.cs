namespace RimGovernor.Brain.Policy;

/// <summary>The bounded journal census, or the exact condition preventing a coherent projection.</summary>
public abstract record ProductionBatchCensus
{
    private ProductionBatchCensus() { }
    public sealed record Read(EquatableArray<ProductionBatch> Batches) : ProductionBatchCensus;
    public sealed record LimitExceeded : ProductionBatchCensus;
    public sealed record AheadOfObservation : ProductionBatchCensus;
}

public enum ProductionPromiseBlock { AwaitingReview, BatchLimit, FutureAction, UnpricedRecipe, EditedBill, NoIngredientAlternative, TruncatedWork }
/// <summary>All production promises must be assessed together: a missing recipe can consume any resource.</summary>
public abstract record ProductionPromiseSet
{
    private ProductionPromiseSet() { }
    public sealed record Assessed(EquatableArray<ForecastCommitment> Promises) : ProductionPromiseSet;
    public sealed record Blocked(ProductionPromiseBlock Reason) : ProductionPromiseSet;
}

/// <summary>An exact amount or an open obligation, such as alternative ingredients or a non-count bill.</summary>
public abstract record MaterialUnits
{
    private MaterialUnits() { }
    public sealed record Exact(double Count) : MaterialUnits;
    public sealed record Open : MaterialUnits;
}

public enum MaterialBudgetBlock { AwaitingReview, ProductionPromises, WorkClaims }
/// <summary>Balances assessed per resource, or a review that cannot establish any material budget.</summary>
public abstract record MaterialAvailability
{
    private MaterialAvailability() { }
    public sealed record Assessed(IReadOnlyDictionary<Resource, MaterialUnits> Balances) : MaterialAvailability;
    public sealed record Blocked(MaterialBudgetBlock Reason) : MaterialAvailability;
}

public abstract record TradeRetention
{
    private TradeRetention() { }
    public sealed record Assessed(IReadOnlyDictionary<Resource, long> Stock) : TradeRetention;
    public sealed record AwaitingConsumption : TradeRetention;
    public sealed record OpenMaterialObligation : TradeRetention;
}
