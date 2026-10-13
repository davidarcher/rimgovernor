namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The zone case family (Go <c>cases/zone</c>).</summary>
public static class ZoneCases
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ZoneRectangle.Case());
        registry.Register(ZoneDelete.Case());
    }
}
