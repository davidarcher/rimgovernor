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
        var tier = record.Tier(PolicyApi.TierMortars);
        if (tier is null || !tier.Built)
        {
            return 0;
        }
        return tier.Buildings.Count;
    }

    internal static MortarShells LoadMortarShells(object native, GenerationSnapshot snapshot, CancellationToken ct)
    {
        if (native is not IDefinitionSource source)
        {
            return default;
        }
        var catalog = source.DefinitionCatalog(Boundary.Identity(snapshot), ct);
        return catalog.MortarShells(PolicyApi.MortarSafeRadius);
    }

    internal static IReadOnlyList<Amount> ShellTargets(object native, StoreJournal journal, GenerationSnapshot snapshot, ColonyProjection projection, CancellationToken ct)
    {
        var mortars = MortarsBuilt(journal, snapshot, ct);
        var shells = LoadMortarShells(native, snapshot, ct);
        return PolicyApi.MortarShellTargets(mortars, PolicyApi.AssessArmory(projection.Facts.RaidPoints, projection.Facts.Research), shells);
    }
}
