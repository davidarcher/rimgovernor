using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Production observation reads over typed requests and replies: the reads a case checks game state through instead of
/// a private test probe. The typed session (<see cref="TypedSession"/>) reads through these.
/// </summary>
public static class TypedReads
{
    /// <summary>A read scope pinned to <paramref name="identity"/>.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.ReadScope Scope(RimGovernor.Brain.GameFacts.Common.Identity identity) => new() { ExpectedIdentity = identity };

    /// <summary>A read reply's <c>observed</c> arm; any other arm fails <paramref name="label"/> with the reply.</summary>
    public static T Observed<TReply, T>(string label, NativeOp op, TReply reply, T? observed)
        where TReply : class
        where T : class
    {
        ArgumentNullException.ThrowIfNull(op);
        return observed ?? throw new AcceptanceException($"{label}: {op.Name} did not observe: {OrdersJson.Serialize(reply)}");
    }

    /// <summary>The <c>observations_list_buildings</c> rows <paramref name="request"/> selects (its scope, ids, defNames, statuses, anyFaction).</summary>
    public static async Task<IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.BuildingState>> BuildingsAsync(this Harness harness, string label, RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var snapshot = await harness.CallAsync(label, NativeOps.ListBuildings, request, cancellationToken).ConfigureAwait(false);
        return snapshot.Buildings.ToList();
    }

    /// <summary>Every zone row <c>observations_list_zones</c> answers under <paramref name="scope"/>.</summary>
    public static async Task<IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.ZoneState>> ZonesAsync(this Harness harness, string label, RimGovernor.Brain.GameFacts.Observations.ReadScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var reply = await harness.CallAsync(label, NativeOps.ListZones, new RimGovernor.Brain.GameOrders.Observations.ListZonesRequest { Scope = scope }, cancellationToken).ConfigureAwait(false);
        return reply.Zones.ToList();
    }

    /// <summary>
    /// Each growing zone (by load id) with its crop, its cells and the cells carrying a plant of that crop: the zones read
    /// joined to the whole map's cell grid.
    /// </summary>
    public static async Task<IReadOnlyList<GrowingZone>> GrowingZonesAsync(this Harness harness, string label, RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var zones = await harness.ZonesAsync(label + "-zones", Scope(identity), cancellationToken).ConfigureAwait(false);
        var grid = await harness.MapCellsAsync(label + "-cells", identity, cancellationToken).ConfigureAwait(false);
        return GrowingZones(zones, grid);
    }

    /// <summary>Each growing zone of <paramref name="zones"/> with its cells and planted cells on <paramref name="grid"/>.</summary>
    public static IReadOnlyList<GrowingZone> GrowingZones(IEnumerable<RimGovernor.Brain.GameFacts.Observations.ZoneState> zones, MapCellGrid grid)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(grid);
        var byZone = grid.ZoneCells();
        var rows = new List<GrowingZone>();
        foreach (var zone in zones.OfType<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing>())
        {
            var cells = byZone.GetValueOrDefault(zone.Id) ?? [];
            var crop = zone.Crop;
            var planted = cells.Where(c => grid.ThingsAt(c).Any(t => t.Def == crop)).ToList();
            rows.Add(new GrowingZone(zone.Id, crop, cells, planted));
        }

        return rows;
    }

    /// <summary>The cells of a wire rectangle (inclusive corners), row-major; a missing corner fails the read.</summary>
    public static IReadOnlyList<Cell> CellsOf(RimGovernor.Brain.GameFacts.Observations.Rectangle? rectangle, string what)
    {
        var min = CellOf(rectangle?.Minimum, what);
        var max = CellOf(rectangle?.Maximum, what);
        var cells = new List<Cell>();
        for (var z = min.Z; z <= max.Z; z++)
        {
            for (var x = min.X; x <= max.X; x++)
            {
                cells.Add(new Cell(x, z));
            }
        }

        return cells;
    }

    /// <summary>The cell of a wire position; a missing coordinate fails the read.</summary>
    public static Cell CellOf(RimGovernor.Brain.Domain.Cell? position, string what) =>
        position is {} at ? at : throw new AcceptanceException($"{what}: no position");
}

/// <summary>A growing zone's crop, cells and the cells planted with that crop.</summary>
public sealed record GrowingZone(string Id, string Crop, IReadOnlyList<Cell> Cells, IReadOnlyList<Cell> Planted);
