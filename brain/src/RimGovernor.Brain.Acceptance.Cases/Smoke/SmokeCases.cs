namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The cases that prove the runner itself: open a game, read what the runner prepared, pass (Go <c>cases/smoke</c>).</summary>
public static class SmokeCases
{
    /// <summary>Registers every <c>smoke/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(SmokeIdentity.Case());
    }
}
