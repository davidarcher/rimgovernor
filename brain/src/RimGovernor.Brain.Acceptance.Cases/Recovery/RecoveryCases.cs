namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>recovery/*</c> cases (Go <c>cases/recovery</c>).</summary>
public static class RecoveryCases
{
    /// <summary>Registers every <c>recovery/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(RecoveryArea.Case());
        registry.Register(RecoveryService.Case());
    }
}
