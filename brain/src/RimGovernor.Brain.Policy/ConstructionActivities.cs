using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Brain.Policy.Methods.Binding;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    public static PawnActivities? StartConstructionActivity(ConstructionActivityRead read, ulong authority, string method,
        long deadline, WorkResourceView resources, out ForecastActivityOutcomeComparison? comparison)
    {
        comparison = null;
        var candidates = ConstructionMethodCandidates.Generate(read, deadline, resources);
        if (candidates.Problem is null || read.Context?.Tick is not { } tick) return null;
        comparison = CompareActivityOutcomes(candidates.Problem, candidates.Outcomes, ForecastOutcomeObjective.LowestLaborPerUnit,
            generationTruncated: candidates.Truncated);
        return CoordinatedActivityBinding.Start(comparison, candidates.Bindings, authority, method, tick);
    }
}
