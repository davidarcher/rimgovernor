namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsMech
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
            hostile = hostile || (t.Kind == ThreatKind.Hostile && !t.Dead && !t.Downed);
        }

        var chargerReady = read.Projection.BiotechSection is PbObservations.BiotechSection.Active { Facts: var biotech } && MechRules.MechChargerReady(MechRules.MechChargersOf(biotech));
        return MechRules.PlanMechControl(MechCatalogOf(read.Catalog), fleet.Mechanitors, fleet.Mechs, hostile, chargerReady);
    }

    internal static MechGuardPlan CombatMechGuards(Combat combat, IReadOnlyList<string> hostileIds, IReadOnlyDictionary<string, O.PawnState> rows)
    {
        var fleet = RoundsContainmentReads.MechFleetOf(combat.Detail.Table.Values());
        if (fleet.Mechanitors.Count == 0 || fleet.Mechs.Count == 0)
        {
            return new MechGuardPlan();
        }

        var hostiles = new List<MechHostile>();
        foreach (var id in hostileIds)
        {
            if (!rows.TryGetValue(id, out var row) || row is null || row.Dead || row.Downed)
            {
                continue;
            }

            if (RoundsContainmentReads.RowCellOf(row).TryGetValue(out var cell))
            {
                hostiles.Add(new MechHostile { Id = new PawnId(id), Cell = cell });
            }
        }

        return MechRules.PlanMechGuards(MechCatalogOf(combat.Catalog), fleet.OverseerCells, fleet.Mechs, hostiles);
    }
}
