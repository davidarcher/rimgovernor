namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Drops plant sources standing in a growing zone: one observed in the planning window, or one a growing-zone
    /// create of this world planned, open or completed (a pending one carries no snapshot yet). Native refuses a
    /// plant acquisition inside a growing zone, and the census can predate a zone the fields plan just created.
    /// Hunts move and are kept.
    /// </summary>
    internal static Fact<IReadOnlyList<AcquisitionSource>> WithoutFieldSources(Fact<IReadOnlyList<AcquisitionSource>> sources, ColonyProjection projection, IReadOnlyList<PlanState> plans, GenerationSnapshot current)
    {
        if (!sources.TryGetValue(out var rows))
        {
            return sources;
        }
        var fields = FieldCells(projection, plans, current);
        if (fields.Count == 0)
        {
            return sources;
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
        return Known<IReadOnlyList<AcquisitionSource>>(kept);
    }

    internal static HashSet<Cell> FieldCells(ColonyProjection projection, IReadOnlyList<PlanState> plans, GenerationSnapshot current)
    {
        var cells = new HashSet<Cell>();
        var farms = new HashSet<string>(projection.Farms.Select(f => f.Id), StringComparer.Ordinal);
        foreach (var cell in projection.Cells)
        {
            if (cell.ZoneId.TryGetValue(out var id) && farms.Contains(id))
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
                if (zone is null || zone.Kind != ZoneKind.Growing || v.Stage == Stage.Cancelled || v.Stage == Stage.Unsuccessful || (v.Snapshot != default && !v.Snapshot.SameWorld(current)))
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
