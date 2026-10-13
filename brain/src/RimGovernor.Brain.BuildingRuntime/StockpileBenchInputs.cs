namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The benches consuming stored inputs, read from the bench census and the built-building census for each
    /// bench's cell. The kitchen's and butcher's benches (the projection's production benches) are left to the
    /// food stores; with that census or the building census unobserved no bench is planned.
    /// </summary>
    internal static IReadOnlyList<Pol.BenchInput> BenchInputs(IReadOnlyList<GearBenchRead> census, Observation.ColonyProjection projection)
    {
        if (!projection.Facts.CurrentConstruction.TryGetValue(out var built) || !built.Colony
            || !projection.ProductionBenches.TryGetValue(out var food))
        {
            return [];
        }
        var at = new Dictionary<string, Cell>(built.Buildings.Count, StringComparer.Ordinal);
        foreach (var b in built.Buildings)
        {
            at[b.Id] = b.Building.Cell;
        }
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in food)
        {
            excluded.Add(b.Id);
        }
        var benches = new List<Pol.GearBench>(census.Count);
        foreach (var row in census)
        {
            benches.Add(row.Bench);
        }
        return Pol.BenchInput.Derive(benches, at, excluded);
    }
}
