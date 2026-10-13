namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>
    /// The finished buildings of the frame's building table whose ThingDef a held ritual's pattern requires: the
    /// cells the game offers the begin command at. The required defs are the catalog's ritual defs, never a list
    /// here. Unknown without the ideoligion or the building table.
    /// </summary>
    internal static Fact<EquatableArray<RitualSite>> RitualSitesOf(BuildingCensus? census, Fact<Ideoligion> ideology)
    {
        if (!ideology.TryGetValue(out var ideo) || census is null)
        {
            return Unknown<EquatableArray<RitualSite>>();
        }
        var wanted = new List<string>();
        foreach (var held in ideo.Facts.Rituals)
        {
            if (ideo.Defs.Rituals.TryGetValue(held.Pattern, out var pattern))
            {
                wanted.AddRange(pattern.RequiredBuildings);
            }
        }
        var output = new List<RitualSite>();
        if (wanted.Count == 0)
        {
            return Known(EquatableArray.From(output));
        }
        foreach (var row in census.Rows.Table.Values())
        {
            var def = row.Building?.DefName ?? "";
            if (row.Status != O.BuildingStatus.Built || !wanted.Contains(def))
            {
                continue;
            }
            if (!RowCell(row.Building?.Position).TryGetValue(out var cell))
            {
                return Unknown<EquatableArray<RitualSite>>();
            }
            output.Add(new RitualSite { Id = row.Building?.Id ?? "", Def = def, Cell = cell });
        }
        return Known(EquatableArray.From(output));
    }

    /// <summary>A cell fact: unknown unless both coordinates are present.</summary>
    internal static Fact<Cell> RowCell(C.Cell? cell) =>
        cell is null || !cell.HasX || !cell.HasZ ? Unknown<Cell>() : Known(new Cell(cell.X, cell.Z));
}
