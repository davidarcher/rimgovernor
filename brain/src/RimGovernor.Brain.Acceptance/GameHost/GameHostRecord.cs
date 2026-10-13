using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <c>endpoint.json</c> record of a launched game (Go <c>gamehost.Endpoint</c>): <c>StateDir/&lt;gameId&gt;/endpoint.json</c>
/// names the process by pid plus start time so a reused pid is never mistaken for the game. The GABP port and token of the
/// Go record are gone: the mod's local HTTP channel replaces the bridge.
/// </summary>
public sealed record GameHostRecord(string GameId, int Pid, long PidStartTime, DateTimeOffset LaunchedAt, long Generation)
{
    /// <summary>The endpoint.json layout version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The record's JSON text (the layout <see cref="Parse"/> reads).</summary>
    public string ToJson()
    {
        var node = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["gameId"] = GameId,
            ["pid"] = Pid,
            ["pidStartTime"] = PidStartTime,
            ["launchedAt"] = LaunchedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["generation"] = Generation,
        };
        return node.ToJsonString(Indented) + "\n";
    }

    /// <summary>Parses an endpoint.json body; a record with another schema version is null (stale).</summary>
    public static GameHostRecord? Parse(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject obj)
        {
            throw new JsonException("endpoint record is not a JSON object");
        }

        if (Int(obj["schemaVersion"]) != SchemaVersion)
        {
            return null;
        }

        var launched = DateTimeOffset.TryParse((string?)obj["launchedAt"] ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : DateTimeOffset.MinValue;
        return new GameHostRecord(
            (string?)obj["gameId"] ?? "",
            (int)Int(obj["pid"]),
            Int(obj["pidStartTime"]),
            launched,
            Int(obj["generation"]));
    }

    internal static JsonSerializerOptions Indented { get; } = new() { WriteIndented = true };

    private static long Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}
