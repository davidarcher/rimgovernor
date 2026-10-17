using System.Text.Json.Nodes;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// One power read: the listed buildings' rows by id and every built player generator (a power producer), both from
/// <c>observations_list_buildings</c>.
/// </summary>
internal sealed record PowerRead(IReadOnlyDictionary<string, Observations.BuildingState> Rows, IReadOnlyList<Observations.BuildingState> Generators)
{
    /// <summary>The read as report rows: each building's def, cell and power, fuel and wind service facts.</summary>
    internal JsonObject ToReport() => new()
    {
        ["buildings"] = new JsonArray([.. Rows.Values.Select(Row)]),
        ["generators"] = new JsonArray([.. Generators.Select(g => (JsonNode?)PowerKit.Id(g))]),
    };

    private static JsonNode Row(Observations.BuildingState b) => new JsonObject
    {
        ["id"] = PowerKit.Id(b),
        ["defName"] = b.Building?.DefName,
        ["x"] = b.Building?.Position?.X,
        ["z"] = b.Building?.Position?.Z,
        ["connected"] = b.Service?.Connected,
        ["powerOn"] = b.Service?.PowerOn,
        ["powerOutputW"] = b.Service?.PowerOutputW,
        ["producer"] = b.Service?.Producer,
        ["fuel"] = b.Service?.Fuel,
        ["outOfFuel"] = b.Service?.OutOfFuel,
        ["windPathBlockedCells"] = b.Service?.WindPathBlockedCells,
    };
}

/// <summary>What the power cases share (Go: helpers of <c>power/power.go</c>): the native power read and its rows.</summary>
internal static class PowerKit
{
    internal const string Prefix = "power-accept";

    /// <summary>The building's id; a row without one fails the read.</summary>
    internal static string Id(Observations.BuildingState row) => row.Building?.Id ?? throw new AcceptanceException("building row without an id");

    /// <summary>One read of <paramref name="ids"/> and the built player generators.</summary>
    internal static async Task<PowerRead> ObserveAsync(Harness h, JsonObject identity, IReadOnlyCollection<string> ids, string label, CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, Observations.BuildingState>(StringComparer.Ordinal);
        if (ids.Count > 0)
        {
            var listed = await h.BuildingsAsync(label, identity, new JsonObject { ["ids"] = new JsonArray([.. ids.Select(id => (JsonNode?)id)]) }, cancellationToken).ConfigureAwait(false);
            foreach (var row in listed)
            {
                rows[Id(row)] = row;
            }
        }

        var built = await h.BuildingsAsync(label + "-generators", identity, new JsonObject { ["statuses"] = new JsonArray("built"), ["playerOnly"] = true }, cancellationToken).ConfigureAwait(false);
        return new PowerRead(rows, [.. built.Where(b => b.Service?.Producer == true)]);
    }

    /// <summary>Reports whether every listed consumer row reads powered.</summary>
    internal static bool AllPowered(PowerRead read, IEnumerable<string> ids) =>
        ids.All(id => read.Rows.GetValueOrDefault(id)?.Service?.PowerOn == true);

    /// <summary>Lets the paused game run a few seconds at Normal speed, then pauses it again.</summary>
    internal static async Task RunBrieflyAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        _ = await h.CallAsync("resume-" + label, "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Normal", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("pause-" + label, "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
    }
}
