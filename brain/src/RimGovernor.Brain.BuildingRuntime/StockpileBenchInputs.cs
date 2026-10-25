namespace RimGovernor.Brain.BuildingRuntime;

internal static class StockpileBenchInputs
{
    /// <summary>
    /// The benches consuming stored inputs, read from the bench census and the built-building census for each
    /// bench's cell. The kitchen's and butcher's benches (the projection's production benches) are left to the
    /// food stores; with that census or the building census unobserved no bench is planned.
    /// </summary>
    internal static IReadOnlyList<Pol.BenchInput> BenchInputs(IReadOnlyList<GearBenchRead> census, Observation.ColonyProjection projection)
    {
        var built = projection.Facts.CurrentConstruction;
        var at = new Dictionary<string, Cell>(built.Buildings.Count, StringComparer.Ordinal);
        foreach (var b in built.Buildings)
        {
            if (b.Building is { } placed)
            {
                at[b.Id] = placed.Cell;
            }
        }
        var excluded = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var b in projection.ProductionBenches)
        {
            excluded[b.Id] = true;
        }
        var benches = new List<Pol.GearBench>(census.Count);
        foreach (var row in census)
        {
            benches.Add(row.Bench);
        }
        return Pol.StorageRules.DeriveBenchInputs(benches, at, excluded);
    }
}
