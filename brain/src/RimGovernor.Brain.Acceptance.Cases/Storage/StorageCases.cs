namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The storage hauling cases (Go <c>cases/storage</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class StorageCases
{
    /// <summary>Registers every <c>storage/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(StorageStrandedLoot.Case());
        registry.Register(StorageYardShelf.Case());
        registry.Register(StorageOccupancyRing.Case());
    }
}
