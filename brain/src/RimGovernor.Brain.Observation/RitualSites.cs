namespace RimGovernor.Brain.Observation;

public static class RitualSitesReads
{
    /// <summary>
    /// The finished buildings of the frame's building table whose ThingDef a held ritual's pattern requires: the
    /// cells the game offers the begin command at. The required defs are the catalog's ritual defs, never a list
    /// here. None without the Ideology expansion; unknown without the building table.
    /// </summary>
    internal static Fact<EquatableArray<RitualSite>> RitualSitesOf(BuildingCensus? census, IdeologyMode ideology)
    {
        if (ideology is not IdeologyMode.Installed { Primary: var ideo })
        {
            return Known(EquatableArray.From(new List<RitualSite>()));
        }
        if (census is null)
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
            var def = row.Building.DefName;
            if (row.Status != O.BuildingStatus.Built || !wanted.Contains(def))
            {
                continue;
            }
            output.Add(new RitualSite { Id = row.Building.Id, Def = def });
        }
        return Known(EquatableArray.From(output));
    }

    /// <summary>A row head's cell as the policy's cell fact: unknown for an unplaced head.</summary>
    internal static Fact<Cell> RowCell(O.EntityPlace place) =>
        place is O.EntityPlace.OnMap at ? Known(new Cell(at.Cell.X, at.Cell.Z)) : Unknown<Cell>();
}
