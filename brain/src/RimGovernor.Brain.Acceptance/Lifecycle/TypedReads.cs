using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Production observation reads decoded into their wire records: the reads a case checks game state through instead of
/// a private test probe.
/// </summary>
public static class TypedReads
{
    /// <summary>A read scope pinned to <paramref name="identity"/>.</summary>
    public static JsonObject Scope(JsonNode identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new JsonObject { ["expectedIdentity"] = identity.DeepClone() };
    }

    /// <summary>One read under <paramref name="identity"/> (its request's <c>scope</c>), its <c>observed</c> arm decoded as <typeparamref name="T"/>.</summary>
    public static async Task<T> ObservedAsync<T>(this Harness harness, string label, string method, JsonNode identity, JsonObject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(request);
        request["scope"] = Scope(identity);
        var reply = await harness.WireAsync(label, method, request, cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, observed) = Harness.Outcome(reply, "observed");
            return WireCodec.ToRecord<T>(observed);
        }
        catch (Exception e) when (e is AcceptanceException or JsonException)
        {
            throw new AcceptanceException($"{label}: {method}: {e.Message}", e);
        }
    }

    /// <summary>The <c>observations_list_buildings</c> rows <paramref name="filter"/> selects (its ids, defNames, statuses, playerOnly).</summary>
    public static async Task<IReadOnlyList<Observations.BuildingState>> BuildingsAsync(this Harness harness, string label, JsonNode identity, JsonObject filter, CancellationToken cancellationToken)
    {
        var snapshot = await harness.ObservedAsync<Observations.BuildingsSnapshot>(label, "observations_list_buildings", identity, filter, cancellationToken).ConfigureAwait(false);
        return snapshot.Buildings.ToList();
    }

    /// <summary>Every zone row <c>observations_list_zones</c> answers.</summary>
    public static async Task<IReadOnlyList<Observations.ZoneState>> ZonesAsync(this Harness harness, string label, JsonNode identity, CancellationToken cancellationToken)
    {
        var snapshot = await harness.ObservedAsync<Observations.ZonesSnapshot>(label, "observations_list_zones", identity, [], cancellationToken).ConfigureAwait(false);
        return snapshot.Zones.ToList();
    }

    /// <summary>
    /// Each growing zone (by load id) with its crop, its cells and the cells carrying a plant of that crop: the zones read
    /// joined to the whole map's cell grid.
    /// </summary>
    public static async Task<IReadOnlyList<GrowingZone>> GrowingZonesAsync(this Harness harness, string label, JsonNode identity, CancellationToken cancellationToken)
    {
        var zones = await harness.ZonesAsync(label + "-zones", identity, cancellationToken).ConfigureAwait(false);
        var grid = await harness.MapCellsAsync(label + "-cells", identity, cancellationToken).ConfigureAwait(false);
        return GrowingZones(zones, grid);
    }

    /// <summary>Each growing zone of <paramref name="zones"/> with its cells and planted cells on <paramref name="grid"/>.</summary>
    public static IReadOnlyList<GrowingZone> GrowingZones(IEnumerable<Observations.ZoneState> zones, MapCellGrid grid)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(grid);
        var byZone = grid.ZoneCells();
        var rows = new List<GrowingZone>();
        foreach (var zone in zones)
        {
            if (!string.Equals(zone.Type, "growing", StringComparison.OrdinalIgnoreCase) || zone.Id is null)
            {
                continue;
            }

            var cells = byZone.GetValueOrDefault(zone.Id) ?? [];
            var crop = zone.CropDefName ?? "";
            var planted = cells.Where(c => crop.Length > 0 && grid.ThingsAt(c).Any(t => t.Def == crop)).ToList();
            rows.Add(new GrowingZone(zone.Id, crop, cells, planted));
        }

        return rows;
    }

    /// <summary>The cells of a wire rectangle (inclusive corners), row-major; a missing corner fails the read.</summary>
    public static IReadOnlyList<Cell> CellsOf(Observations.Rectangle? rectangle, string what)
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
    public static Cell CellOf(Common.Cell? position, string what) =>
        position is { X: int x, Z: int z } ? new Cell(x, z) : throw new AcceptanceException($"{what}: no position");
}

/// <summary>A growing zone's crop, cells and the cells planted with that crop.</summary>
public sealed record GrowingZone(string Id, string Crop, IReadOnlyList<Cell> Cells, IReadOnlyList<Cell> Planted);
