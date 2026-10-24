using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsArmoryShells
{
    /// <summary>The count of built mortars in the defense layout record; zero without a record.</summary>
    internal static int MortarsBuilt(StoreJournal journal, GenerationSnapshot snapshot, CancellationToken ct)
    {
        var record = journal.LoadDefenseLayout(new StoreWorld(snapshot.Colony, snapshot.Load, snapshot.Map), ct);
        if (record is null)
        {
            return 0;
        }
        var (_, buildings, found) = record.Tier(DefenseMortarsPolicy.TierMortars);
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
        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        return catalog.MortarShells(CombatMortarRules.MortarSafeRadius);
    }

    internal static async Task<IReadOnlyList<Amount>> ShellTargetsAsync(object native, StoreJournal journal, GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        var mortars = MortarsBuilt(journal, snapshot, ct);
        var shells = await LoadMortarShellsAsync(native, snapshot, ct).ConfigureAwait(false);
        return ArmoryShellsRules.MortarShellTargets(mortars, ArmoryRules.AssessArmory(projection.Facts.RaidPoints, projection.Facts.DefenseCapacity, projection.Facts.Research), shells);
    }
}
