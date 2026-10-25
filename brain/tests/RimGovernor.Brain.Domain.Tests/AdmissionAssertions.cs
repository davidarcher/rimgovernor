using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

internal static class AdmissionAssertions
{
    internal static ProductionPromiseSet MaterialPromises(EquatableArray<(string Id, EquatableArray<ForecastMaterialClaim> Claims)> promises) =>
        new ProductionPromiseSet.Assessed(EquatableArray.From(promises.Select(p => new ForecastCommitment(p.Id, default) { MaterialClaims = p.Claims })));
    internal static double ExactCount(this MaterialUnits units) => Assert.IsType<MaterialUnits.Exact>(units).Count;
    internal static double ExactCount(this ForecastMaterialClaim claim) => claim.Units.ExactCount();
    internal static IReadOnlyDictionary<Resource, MaterialUnits> AssessedBalances(this MaterialAvailability value) => Assert.IsType<MaterialAvailability.Assessed>(value).Balances;
    internal static EquatableArray<ForecastCommitment> AssessedPromises(this ProductionPromiseSet value) => Assert.IsType<ProductionPromiseSet.Assessed>(value).Promises;
    internal static EquatableArray<ProductionBatch> ReadBatches(this ProductionBatchCensus value) => Assert.IsType<ProductionBatchCensus.Read>(value).Batches;
    internal static IReadOnlyDictionary<Resource, long> RetainedStock(this TradeRetention value) => Assert.IsType<TradeRetention.Assessed>(value).Stock;
}
