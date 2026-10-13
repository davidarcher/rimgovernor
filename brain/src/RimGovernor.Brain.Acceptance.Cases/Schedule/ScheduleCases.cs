namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>schedule/*</c> cases (Go <c>cases/schedule</c>).</summary>
public static class ScheduleCases
{
    /// <summary>Registers every <c>schedule/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ScheduleTimetables.TimetablesCase());
        registry.Register(ScheduleTimetables.MeditateCase());
    }
}
