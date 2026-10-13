namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The native stockpile zones of a complete zones read; null when the read is partial, so no zoned cell is
    /// taken for a stockpile.
    /// </summary>
    internal static HashSet<string>? StockpileZoneIds(Observation.ColonyProjection projection)
    {
        if (projection.Zones is not { Complete: true } zones)
        {
            return null;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in zones.Value.Rows)
        {
            if ((row.Type ?? "") == "stockpile")
            {
                ids.Add((row.Id ?? ""));
            }
        }
        return ids;
    }
}
