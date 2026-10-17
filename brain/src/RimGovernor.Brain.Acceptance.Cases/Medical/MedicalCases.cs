namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>medical/*</c> acceptance cases: the native health, tend and surgery reads and the medical bill.</summary>
public static class MedicalCases
{
    /// <summary>Registers every <c>medical/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(PlagueReadback.Case());
        registry.Register(TendGates.Case());
        foreach (var surgery in MedicalSurgery.All())
        {
            registry.Register(surgery);
        }
    }
}
