namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>mapscope/*</c> acceptance cases.</summary>
public static class MapscopeCases
{
    /// <summary>Registers every <c>mapscope/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(MapscopeIsolation.Case());
    }
}
