using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What the power cases share (Go: helpers of <c>power/power.go</c>): the native power observation and its rows.</summary>
internal static class PowerKit
{
    internal const string Prefix = "power-accept";

    /// <summary>One <c>test/power_observe</c> read of <paramref name="ids"/>; a refusal fails the case.</summary>
    internal static async Task<JsonObject> ObserveAsync(Harness h, IEnumerable<string> ids, string label, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(label, "test/power_observe", new JsonObject { ["ids"] = string.Join(',', ids) }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{label}: power_observe refused: {reply.ToJson()}");
        }

        return reply;
    }

    /// <summary>Reports whether every listed consumer row reads powerOn.</summary>
    internal static bool AllPowered(Dictionary<string, JsonObject> rows, IEnumerable<string> ids) =>
        ids.All(id => rows.GetValueOrDefault(id)?["powerOn"].AsBool() == true);

    /// <summary>The observed building rows by id.</summary>
    internal static Dictionary<string, JsonObject> IndexRows(JsonObject reply)
    {
        var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var raw in reply["buildings"].AsSlice())
        {
            if (raw.AsMap() is { } row)
            {
                rows[row["id"].AsString()] = row;
            }
        }

        return rows;
    }

    /// <summary>Lets the paused game run a few seconds at Normal speed, then pauses it again.</summary>
    internal static async Task RunBrieflyAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        _ = await h.CallAsync("resume-" + label, "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Normal", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("pause-" + label, "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
    }
}
