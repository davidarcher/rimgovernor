using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <c>endpoint.json</c> record of a launched game (Go <c>gamehost.Endpoint</c>): <c>StateDir/&lt;gameId&gt;/endpoint.json</c>
/// names the process by pid plus start time so a reused pid is never mistaken for the game. The record carries no port or token:
/// the mod's local HTTP channel is found through its own endpoint file.
/// </summary>
public sealed record GameHostRecord(string GameId, int Pid, long PidStartTime, DateTimeOffset LaunchedAt, long Generation)
{
    /// <summary>The endpoint.json layout version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The record's JSON text (the layout <see cref="Parse"/> reads).</summary>
    public string ToJson() => LaunchJson.Endpoint(new GameEndpointFile
    {
        SchemaVersion = SchemaVersion,
        GameId = GameId,
        Pid = Pid,
        PidStartTime = PidStartTime,
        LaunchedAt = LaunchedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        Generation = Generation,
    }) + "\n";

    /// <summary>Parses an endpoint.json body; a record with another schema version is null (stale).</summary>
    public static GameHostRecord? Parse(string text)
    {
        var file = LaunchJson.ParseEndpoint(text);
        if (file.SchemaVersion != SchemaVersion)
        {
            return null;
        }

        var launched = DateTimeOffset.TryParse(file.LaunchedAt ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : DateTimeOffset.MinValue;
        return new GameHostRecord(file.GameId ?? "", (int)file.Pid, file.PidStartTime, launched, file.Generation);
    }
}
