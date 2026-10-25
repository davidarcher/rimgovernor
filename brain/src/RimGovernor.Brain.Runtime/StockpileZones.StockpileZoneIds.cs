namespace RimGovernor.Brain.Runtime;

partial class StockpileZones
{
    /// <summary>
    /// The native stockpile zones of a complete zones read; null when the read is partial, so no zoned cell is
    /// taken for a stockpile.
    /// </summary>
    internal static HashSet<string>? StockpileZoneIds(ColonyReading projection)
    {
        if (projection.Zones is not { Complete: true } zones)
        {
            return null;
        }
        return [.. zones.Value.Zones.OfType<RimGovernor.Brain.GameFacts.Observations.ZoneState.Stockpile>().Select(row => row.Id)];
    }
}
