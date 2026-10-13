using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The zone edit's observation_invalidated check (Go <c>invalidation.go</c>).</summary>
internal static class ZoneInvalidation
{
    internal const string SessionOwner = "native-zone-acceptance";

    /// <summary>
    /// Bounds the clock window an edit runs under; the window is paused as soon as the journal carries the
    /// invalidation, so the budget only matters when it never arrives.
    /// </summary>
    private const int WindowTicks = 1800;

    /// <summary>Bounds the polling spent waiting for the journal row.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs <paramref name="execute"/> while a supervised clock window plays and returns the observation_invalidated
    /// row the native probe journals for the zone: the colony family narrowed to the zone's id and one rectangle
    /// covering cells. The window is paused afterwards; the caller's later dispatch runs on a paused game as before.
    /// </summary>
    internal static async Task<JsonObject> EditUnderEpochAsync(
        Harness h,
        JsonObject identity,
        Report report,
        string zoneId,
        IReadOnlyList<JsonObject> cells,
        Func<Task> execute,
        CancellationToken cancellationToken)
    {
        var supervisor = new ScenarioClock(h.WireFunc(), identity, SessionOwner, report);
        _ = await supervisor.AcquireAsync("invalidation-acquire", cancellationToken).ConfigureAwait(false);

        // The native journal opens lazily; an events read opens it so the status below reports the retained
        // watermark (its reply is irrelevant here).
        _ = await h.WireAsync(
            "invalidation-prime",
            "clock_read_events",
            new JsonObject { ["identity"] = identity.DeepClone(), ["afterCursor"] = "0", ["limit"] = 1 },
            cancellationToken).ConfigureAwait(false);
        var statusReply = await h.WireAsync(
            "invalidation-cursor",
            "clock_read_status",
            new JsonObject { ["identity"] = identity.DeepClone() },
            cancellationToken).ConfigureAwait(false);
        var (_, status) = Harness.Outcome(statusReply, "status");
        if (!status.ContainsKey("newestCursor"))
        {
            throw new AcceptanceException($"invalidation-cursor: clock status reports no journal watermark: {status.ToJson()}");
        }

        var cursor = ScenarioProjection.ScenarioInteger(status["newestCursor"]);
        var started = await supervisor.ControlAsync(
            "start",
            new JsonObject
            {
                ["authority"] = new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["expectedGeneration"] = Authority.GrantGeneration(supervisor.Grant ?? []),
                    ["attempt"] = new JsonObject
                    {
                        ["controllerSessionId"] = SessionOwner,
                        ["actionId"] = "zone-invalidation-start",
                        ["attemptId"] = "1",
                    },
                },
                ["speed"] = "SPEED_NORMAL",
                ["policy"] = new JsonObject
                {
                    ["mode"] = "WATCH_MODE_COLONY",
                    ["healthDropFraction"] = 0.1,
                    ["minHealthFraction"] = 0.5,
                    ["hostileWithin"] = 40,
                    ["injuryStopCooldownMs"] = 0,
                },
                ["leaseMs"] = 30000,
                ["maxTicks"] = WindowTicks,
            },
            cancellationToken).ConfigureAwait(false);
        var epoch = started["epoch"]?.DeepClone();
        await execute().ConfigureAwait(false);

        var deadline = DateTime.UtcNow + Wait;
        JsonObject? invalidated = null;
        while (invalidated is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AcceptanceException($"no observation_invalidated naming zone {zoneId} within {Wait} of the edit");
            }

            var reply = await h.WireAsync(
                "invalidation-events",
                "clock_read_events",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["afterCursor"] = cursor.ToString(CultureInfo.InvariantCulture),
                    ["limit"] = 128,
                },
                cancellationToken).ConfigureAwait(false);
            var (_, page) = Harness.Outcome(reply, "page");
            if (page["gap"].AsBool() == true)
            {
                throw new AcceptanceException($"invalidation-events: unexpected journal gap: {page.ToJson()}");
            }

            cursor = ScenarioProjection.ScenarioInteger(page["nextCursor"]);
            foreach (var raw in page["events"].AsSlice())
            {
                var evt = raw.AsMap() ?? [];
                if (evt["stopped"].AsMap() is { } stop)
                {
                    throw new AcceptanceException($"the window stopped ({stop["reason"].AsString()}) before the journal named the zone edit: {stop.ToJson()}");
                }

                if (evt["observationInvalidated"].AsMap() is not { } row)
                {
                    continue;
                }

                if (AsStrings(row["entityIds"]).Contains(zoneId, StringComparer.Ordinal))
                {
                    invalidated = row;
                }
            }

            if (invalidated is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }

        _ = await supervisor.CallAsync(
            "pause",
            new JsonObject { ["owner"] = SessionOwner, ["epoch"] = epoch },
            cancellationToken).ConfigureAwait(false);
        CheckInvalidation(invalidated, zoneId, cells);
        report["zone_invalidation"] = invalidated.DeepClone();
        Console.WriteLine($"PASS observation_invalidated names zone {zoneId} over {invalidated["cells"].ToJson()}");
        return invalidated;
    }

    /// <summary>
    /// Asserts the row carries the colony family alone, the zone as its one entity id and a rectangle that covers
    /// every cell the edit touched and lies within the zone's fixture footprint.
    /// </summary>
    private static void CheckInvalidation(JsonObject row, string zoneId, IReadOnlyList<JsonObject> cells)
    {
        var families = AsStrings(row["families"]);
        if (families.Count != 1 || families[0] != "FACT_FAMILY_COLONY")
        {
            throw new AcceptanceException($"observation_invalidated families = [{string.Join(' ', families)}], expected the colony family alone: {row.ToJson()}");
        }

        var ids = AsStrings(row["entityIds"]);
        if (ids.Count != 1 || ids[0] != zoneId)
        {
            throw new AcceptanceException($"observation_invalidated entityIds = [{string.Join(' ', ids)}], expected [{zoneId}]: {row.ToJson()}");
        }

        if (!row["reason"].AsString().Contains(zoneId, StringComparison.Ordinal))
        {
            throw new AcceptanceException($"observation_invalidated reason does not name the zone: {row.ToJson()}");
        }

        if (row["cells"].AsMap() is not { } rect)
        {
            throw new AcceptanceException($"observation_invalidated carries no cells rectangle: {row.ToJson()}");
        }

        var minimum = rect["minimum"].AsMap();
        var maximum = rect["maximum"].AsMap();
        var minX = minimum?["x"].AsNumber() ?? -1;
        var minZ = minimum?["z"].AsNumber() ?? -1;
        var maxX = maximum?["x"].AsNumber() ?? -1;
        var maxZ = maximum?["z"].AsNumber() ?? -1;
        if (minX > maxX || minZ > maxZ)
        {
            throw new AcceptanceException($"observation_invalidated rectangle is not ordered: {rect.ToJson()}");
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
                $"observation_invalidated rectangle ({minX},{minZ})-({maxX},{maxZ}) is not the zone footprint ({fMinX},{fMinZ})-({fMaxX},{fMaxZ})"));
        }
    }

    /// <summary>Projects a decoded JSON array of strings.</summary>
    private static List<string> AsStrings(JsonNode? value) => [.. value.AsSlice().Select(item => item.AsString())];
}
