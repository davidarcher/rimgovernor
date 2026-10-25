namespace RimGovernor.Brain.Policy;

public static partial class WorkRules
{
    /// <summary>Native work definitions may be added by mods; only modeled work types take part in planning.</summary>
    public static WorkType WorkTypeOf(string? name) =>
        Wire.TryParse<WorkType>(name ?? "", out var work) ? work : WorkType.Unrecognized;
}
