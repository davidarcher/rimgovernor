namespace RimGovernor.Brain.Policy;

public sealed record AcquisitionCompetition
{
    /// <summary>
    /// Whether competing urgent work stands. Routine deficits are not a veto. Urgent work holds a candidate
    /// unless a demand it serves is an emergency.
    /// </summary>
    public bool Urgent { get; init; }
}

public static partial class ResourceRules
{
    internal static bool FiniteAcquisitionCost(double v) => double.IsFinite(v) && v >= 0;
}
