namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal static MechCatalog MechCatalogOf(DefinitionCatalog? catalog) =>
        catalog?.Biotech is { } biotech ? biotech.MechCatalog() : new MechCatalog();

    internal static IReadOnlyList<PawnSettings> RoundsMechSettings(RoundsReading read)
    {
        if (!read.Projection.Mechs.TryGetValue(out var fleet) || fleet.Mechanitors.Count == 0)
        {
            return [];
        }

        var hostile = false;
        foreach (var t in read.Emergency.Threats)
        {
            t.Dead.TryGetValue(out var dead);
            t.Downed.TryGetValue(out var downed);
            hostile = hostile || (t.Kind == ThreatKind.Hostile && !dead && !downed);
        }

        var chargerReady = read.Projection.Biotech.TryGetValue(out var biotech) && PolicyFunctions.MechChargerReady(biotech.MechChargerRows());
        return PolicyFunctions.PlanMechControl(MechCatalogOf(read.Catalog), fleet.Mechanitors, fleet.Mechs, hostile, chargerReady);
    }

    internal static MechGuardPlan CombatMechGuards(Combat combat, IReadOnlyList<string> hostileIds, IReadOnlyDictionary<string, O.PawnState> rows)
    {
        var fleet = ObservationPackage.MechFleetOf(combat.Detail.Table.Values());
        if (fleet.Mechanitors.Count == 0 || fleet.Mechs.Count == 0)
        {
            return new MechGuardPlan();
        }

        var hostiles = new List<MechHostile>();
        foreach (var id in hostileIds)
        {
            if (!rows.TryGetValue(id, out var row) || row is null || row.Dead.GetValueOrDefault() || row.Downed.GetValueOrDefault())
            {
                continue;
            }

            if (ObservationPackage.RowCellOf(row).TryGetValue(out var cell))
            {
                hostiles.Add(new MechHostile { Id = new PawnId(id), Cell = cell });
            }
        }

        return PolicyFunctions.PlanMechGuards(MechCatalogOf(combat.Catalog), fleet.Mechanitors, fleet.Mechs, hostiles);
    }
}
