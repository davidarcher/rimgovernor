namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>clearance/*</c> cases (Go <c>cases/clearance</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class ClearanceCases
{
    /// <summary>Registers every <c>clearance/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ClearanceChunkDump.Case());
        registry.Register(ClearancePackReinstall.Case());
        registry.Register(ClearanceSalvageRemote.Case());
        foreach (var recovery in ClearanceRecoveryRemote.Cases())
        {
            registry.Register(recovery);
        }
    }
}
