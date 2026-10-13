namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>The count of built mortars in the defense layout record; zero without a record.</summary>
    internal static int MortarsBuilt(StoreJournal journal, GenerationSnapshot snapshot, CancellationToken ct)
    {
        var record = journal.LoadDefenseLayout(new StoreWorld(snapshot.Colony, snapshot.Load, snapshot.Map), ct);
        if (record is null)
        {
            return 0;
        }
        var (_, buildings, found) = record.Tier(PolicyApi.TierMortars);
        if (!found)
        {
            return 0;
        }
        return buildings.Count;
    }

    internal static async Task<MortarShells> LoadMortarShellsAsync(object native, GenerationSnapshot snapshot, CancellationToken ct)
    {
        if (native is not IDefinitionSource source)
        {
            return new MortarShells();
        }
        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false) ?? throw new InvalidOperationException("native served no definition catalog");
        return catalog.MortarShells(PolicyApi.MortarSafeRadius);
    }

    internal static async Task<IReadOnlyList<Amount>> ShellTargetsAsync(object native, StoreJournal journal, GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        var mortars = MortarsBuilt(journal, snapshot, ct);
        var shells = await LoadMortarShellsAsync(native, snapshot, ct).ConfigureAwait(false);
        return PolicyApi.MortarShellTargets(mortars, PolicyApi.AssessArmory(projection.Facts.RaidPoints, projection.Facts.Research), shells);
    }
}
