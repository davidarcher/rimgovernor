namespace RimGovernor.Brain.Policy;

public sealed record AcquisitionCompetition
{
    /// <summary>
    /// Whether competing urgent work stands. Routine deficits are not a veto. Urgent work holds a candidate
    /// unless a demand it serves is an emergency.
    /// </summary>
    public bool Urgent { get; init; }
}

public static partial class PolicyFunctions
{
    internal static bool FiniteAcquisitionCost(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && v <= 1e12;
}
