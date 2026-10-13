namespace RimGovernor.Brain.Policy;

public sealed record AcquisitionCompetition
{
    /// <summary>
    /// Zero means no competing urgent work. Routine deficits are not a veto. An urgent priority above every
    /// matched demand holds this candidate.
    /// </summary>
    public int UrgentPriority { get; init; }
}

public static partial class PolicyFunctions
{
    internal static bool FiniteAcquisitionCost(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && v <= 1e12;
}
