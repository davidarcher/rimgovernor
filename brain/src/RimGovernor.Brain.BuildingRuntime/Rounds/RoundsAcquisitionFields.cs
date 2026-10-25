namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsAcquisitionFields
{
    /// <summary>
    /// Drops plant sources standing in a growing zone: one observed in the planning window, or one a growing-zone
    /// create of this world planned, open or completed (a pending one carries no snapshot yet). Native refuses a
    /// plant acquisition inside a growing zone, and the census can predate a zone the fields plan just created.
    /// Hunts move and are kept.
    /// </summary>
    internal static EquatableArray<AcquisitionSource> WithoutFieldSources(EquatableArray<AcquisitionSource> rows, ColonyReading projection, IReadOnlyList<PlanState> plans, GenerationSnapshot current)
    {
        var fields = FieldCells(projection, plans, current);
        if (fields.Count == 0)
        {
            return rows;
        }
        var kept = new List<AcquisitionSource>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.Hunt && fields.Contains(row.Cell))
            {
                continue;
            }
            kept.Add(row);
        }
        return [.. kept];
    }

    internal static HashSet<Cell> FieldCells(ColonyReading projection, IReadOnlyList<PlanState> plans, GenerationSnapshot current)
    {
        var cells = new HashSet<Cell>();
        var farms = new HashSet<string>((projection.Tables.Zones ?? throw new RequiredFactException("zones")).Zones.OfType<PbObservations.ZoneState.Growing>().Select(f => f.Id), StringComparer.Ordinal);
        foreach (var cell in projection.PlanningWindow.Cells)
        {
            if (cell.ZoneId is { } id && farms.Contains(id))
            {
                cells.Add(cell.Cell);
            }
        }
        foreach (var plan in plans)
        {
            foreach (var progress in plan.Progress)
            {
                var zone = progress.Action.ZoneCreate;
                var v = progress.View;
                if (zone is null || zone.Kind != Domain.ZoneKind.Growing || v.Stage == Stage.Cancelled || v.Stage == Stage.Unsuccessful || (v.Snapshot != default && !v.Snapshot.SameWorld(current)))
                {
                    continue;
                }
                foreach (var cell in zone.Cells)
                {
                    cells.Add(cell);
                }
            }
        }
        return cells;
    }
}
