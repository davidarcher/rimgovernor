using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The production coupled forecast (<see cref="ForecastCoupled.ForecastCoupledScenarioOf"/>) of a request's commitments
/// and of those with commitments added or removed, each evaluated when first read.</summary>
internal sealed class CoupledForecastKit
{
    private readonly Lazy<ColonyForecastScenario> baseline, proposed;

    private CoupledForecastKit(ColonyForecastRequest request, Dictionary<string, ForecastCommitment> before, Dictionary<string, ForecastCommitment> after)
    {
        baseline = new(() => ForecastCoupled.ForecastCoupledScenarioOf(request, before));
        proposed = new(() => ForecastCoupled.ForecastCoupledScenarioOf(request, after));
    }

    public ColonyForecastScenario Baseline => baseline.Value;

    public ColonyForecastScenario Proposed => proposed.Value;

    /// <summary>Forecasts <paramref name="request"/>'s commitments, and again without <paramref name="remove"/> and with <paramref name="add"/>.</summary>
    public static CoupledForecastKit Forecast(ColonyForecastRequest request, IEnumerable<ForecastCommitment>? add = null, IEnumerable<string>? remove = null)
    {
        var before = ColonyForecastRules.ForecastCommitmentsById(request.Commitments);
        var after = new Dictionary<string, ForecastCommitment>(before, StringComparer.Ordinal);
        foreach (var id in remove ?? [])
            if (!after.Remove(id)) throw new DomainException("forecast removes an absent or repeated commitment: " + id);
        foreach (var (id, commitment) in ColonyForecastRules.ForecastCommitmentsById(add ?? []))
            if (!after.TryAdd(id, commitment)) throw new DomainException("forecast addition duplicates existing commitment: " + id);
        return new(request, before, after);
    }
}
