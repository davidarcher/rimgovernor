using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The zone edit's clock_invalidation check (Go <c>invalidation.go</c>).</summary>
internal static class ZoneInvalidation
{
    /// <summary>Bounds the polling spent waiting for the invalidation row.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs <paramref name="execute"/> while a supervised clock runs and checks the clock_invalidation row native emits
    /// for the zone: the colony family narrowed to the zone's id and one rectangle covering cells. The clock is held
    /// afterwards; the caller's later dispatch runs on a held game as before.
    /// </summary>
    internal static async Task EditUnderRunAsync(
        Harness h,
        JsonObject identity,
        Report report,
        string zoneId,
        IReadOnlyList<JsonObject> cells,
        Func<Task> execute,
        CancellationToken cancellationToken)
    {
        var supervisor = new ScenarioClock(h, identity, report);
        _ = await supervisor.AcquireAsync("invalidation-acquire", cancellationToken).ConfigureAwait(false);
        _ = await supervisor.RunAsync("Normal", cancellationToken).ConfigureAwait(false);
        await execute().ConfigureAwait(false);

        var invalidated = await supervisor.AwaitRowsAsync(
            () =>
            {
                if (supervisor.FirstRunRow(TelemetryKinds.ClockHold) is { } stop)
                {
                    throw new AcceptanceException($"the clock was held ({ClockRows.Reason(stop)}) before native named the zone edit: {ClockRows.Text(stop, "msg")}");
                }

                return supervisor.RunRows(TelemetryKinds.ClockInvalidation).FirstOrDefault(row => ClockRows.Strings(row, "entity_ids").Contains(zoneId, StringComparer.Ordinal));
            },
            Wait,
            $"a clock_invalidation row naming zone {zoneId} after the edit",
            cancellationToken).ConfigureAwait(false);

        _ = await supervisor.HoldAsync("REVIEW", "zone invalidation observed", cancellationToken).ConfigureAwait(false);
        var shown = Shown(invalidated);
        report["zone_invalidation"] = shown;
        CheckInvalidation(invalidated, zoneId, cells, shown.ToJsonString());
        Console.WriteLine($"PASS clock_invalidation names zone {zoneId} over {shown["cells"].ToJson()}");
    }

    /// <summary>The row's invalidation fields, for the report and the failure messages.</summary>
    private static JsonObject Shown(LogRow row)
    {
        var cells = new JsonObject();
        foreach (var (key, value) in ClockRows.Map(row, "cells"))
        {
            cells[key] = ClockRows.Whole(value);
        }

        return new JsonObject
        {
            ["families"] = new JsonArray([.. ClockRows.Strings(row, "families").Select(x => (JsonNode?)x)]),
            ["entity_ids"] = new JsonArray([.. ClockRows.Strings(row, "entity_ids").Select(x => (JsonNode?)x)]),
            ["reason"] = ClockRows.Text(row, ColonyEventTelemetry.ReasonKey),
            ["cells"] = cells,
            ["tick"] = ClockRows.Tick(row),
        };
    }

    /// <summary>
    /// Asserts the row carries the colony family alone, the zone as its one entity id and a rectangle that covers
    /// every cell the edit touched and lies within the zone's fixture footprint.
    /// </summary>
    private static void CheckInvalidation(LogRow row, string zoneId, IReadOnlyList<JsonObject> cells, string shown)
    {
        var families = ClockRows.Strings(row, "families");
        if (families.Count != 1 || families[0] != nameof(Clock.FactFamily.Colony))
        {
            throw new AcceptanceException($"clock_invalidation families = [{string.Join(' ', families)}], expected the colony family alone: {shown}");
        }

        var ids = ClockRows.Strings(row, "entity_ids");
        if (ids.Count != 1 || ids[0] != zoneId)
        {
            throw new AcceptanceException($"clock_invalidation entity_ids = [{string.Join(' ', ids)}], expected [{zoneId}]: {shown}");
        }

        if (!ClockRows.Text(row, ColonyEventTelemetry.ReasonKey).Contains(zoneId, StringComparison.Ordinal))
        {
            throw new AcceptanceException($"clock_invalidation reason does not name the zone: {shown}");
        }

        var rect = ClockRows.Map(row, "cells");
        if (rect.Count == 0)
        {
            throw new AcceptanceException($"clock_invalidation carries no cells rectangle: {shown}");
        }

        var minX = ClockRows.Whole(rect.GetValueOrDefault("min_x")) ?? -1;
        var minZ = ClockRows.Whole(rect.GetValueOrDefault("min_z")) ?? -1;
        var maxX = ClockRows.Whole(rect.GetValueOrDefault("max_x")) ?? -1;
        var maxZ = ClockRows.Whole(rect.GetValueOrDefault("max_z")) ?? -1;
        if (minX > maxX || minZ > maxZ)
        {
            throw new AcceptanceException($"clock_invalidation rectangle is not ordered: {shown}");
        }

        // The rectangle spans the zone's old and new cells: within the fixture footprint, and covering every fixture
        // cell (the edit restored the full footprint, so the union is the footprint itself).
        var fMinX = cells[0]["x"].AsNumber();
        var fMinZ = cells[0]["z"].AsNumber();
        var fMaxX = fMinX;
        var fMaxZ = fMinZ;
        foreach (var cell in cells)
        {
            var x = cell["x"].AsNumber();
            var z = cell["z"].AsNumber();
            fMinX = Math.Min(fMinX, x);
            fMinZ = Math.Min(fMinZ, z);
            fMaxX = Math.Max(fMaxX, x);
            fMaxZ = Math.Max(fMaxZ, z);
        }

        if (minX != fMinX || minZ != fMinZ || maxX != fMaxX || maxZ != fMaxZ)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"clock_invalidation rectangle ({minX},{minZ})-({maxX},{maxZ}) is not the zone footprint ({fMinX},{fMinZ})-({fMaxX},{fMaxZ})"));
        }
    }
}
