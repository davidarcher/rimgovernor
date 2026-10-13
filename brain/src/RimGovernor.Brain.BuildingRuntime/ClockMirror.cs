namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Port of clock_mirror.go: mirror helpers for the planning window. The rows it holds across steps in the
/// recorded mirror, each refresh read whole since #858.
/// </summary>
internal static class ClockMirror
{
    /// <summary>Section name of the review's own mirrored pawn read (#795 step 3).</summary>
    public const string PawnSectionName = "pawns";

    /// <summary>Section name of the review's own mirrored bench read (#795 step 3).</summary>
    public const string BenchSectionName = "benches";

    public static Dictionary<Cell, SiteCell> CellRows(IEnumerable<SiteCell> cells)
    {
        var output = new Dictionary<Cell, SiteCell>();
        foreach (var row in cells)
        {
            output[row.Cell] = row;
        }
        return output;
    }

    /// <summary>Publishes the review frame's things table as the things section keyed by thing id.</summary>
    public static void PublishThings(FactStore m, FactScope scope, Things things, long tick) =>
        FactsApi.PutKeyed(m, scope, BridgeApi.ThingsSection, things.Table, FactsApi.At(tick));

    /// <summary>
    /// Publishes the review frame's pawn table as the pawn section keyed by pawn id; the recording keeps only the
    /// rows that changed.
    /// </summary>
    public static void PublishPawns(FactStore m, FactScope scope, Pawns pawns, long tick) =>
        FactsApi.PutKeyed(m, scope, PawnSectionName, pawns.Table, FactsApi.At(tick));

    /// <summary>
    /// Reads the gear bench census (each bench's bill stack and recipe catalog) and publishes it as the bench
    /// section keyed by bench thing id, stamped with the step's tick.
    /// </summary>
    public static async Task<FactTable<string, GearBenchRead>> PublishBenchesAsync(FactStore m, FactScope scope, IRoundsWorkBenchSource native, PbCommon.Identity id, long tick, CancellationToken ct)
    {
        var census = await native.ReadGearBenchesAsync(id, ct).ConfigureAwait(false);
        var rows = new Dictionary<string, GearBenchRead>(census.Count, StringComparer.Ordinal);
        foreach (var row in census)
        {
            rows[row.Bench.Id] = row;
        }
        return FactsApi.PutTable(m, scope, BenchSectionName, rows, FactsApi.At(tick));
    }

    /// <summary>A bench table in bench id order, as the gear bench census lists it.</summary>
    public static List<GearBenchRead> BenchRows(IReadOnlyDictionary<string, GearBenchRead> rows) =>
        rows.Values.OrderBy(row => row.Bench.Id, StringComparer.Ordinal).ToList();
}
