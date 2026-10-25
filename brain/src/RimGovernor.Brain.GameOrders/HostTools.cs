using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RimGovernor.Brain.GameFacts.Host;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Host;

// The host-level tools the acceptance harness calls by name outside the wire ops (#3410): the RimGovernor host's
// set_time_speed, press_time_speed and load_game_ready. A reply record carries only what a caller reads; the host
// replies with more, which the reader ignores.

/// <summary>
/// <c>set_time_speed</c>'s and <c>press_time_speed</c>'s arguments. <c>press_time_speed</c> runs
/// the in-game time controls' click handler for the named button (#3608): Paused toggles the pause; a speed asks the bot
/// for it while the bot runs the clock, else sets the game's speed.
/// </summary>
public sealed record TimeSpeedRequest
{
    /// <summary>
    /// Paused, Normal, Fast, Superfast, Ultrafast or Hyperfast. The name decides the dev tick boost: Ultrafast is vanilla
    /// 15x with it off, Hyperfast is Ultrafast with it on, capped at 30x by native.
    /// </summary>
    public required string Speed { get; init; }

    /// <summary>Pauses the clock.</summary>
    public static TimeSpeedRequest Paused { get; } = new() { Speed = "Paused" };
}

/// <summary><c>rimgovernor/load_game_ready</c>'s arguments.</summary>
public sealed record LoadGameRequest
{
    /// <summary>The save name without extension, under the profile's Saves.</summary>
    public required string SaveName { get; init; }

    /// <summary>gameData, mapData, currentMap, playable or visual.</summary>
    public string Readiness { get; init; } = "visual";

    /// <summary>The native wait; zero takes the caller's default. The harness widens the call's deadline to cover it.</summary>
    public long TimeoutMs { get; init; }

    /// <summary>Loads a save whose recorded mods are not all active.</summary>
    public bool IgnoreModCompatibility { get; init; }
}

/// <summary>The one JSON entry point for the host-tool records: source-generated, camelCase, unknown reply fields ignored.</summary>
public static class HostJson
{
    /// <summary>Maps typed requests to the host registry's named primitive arguments.</summary>
    public static JsonObject Arguments(TimeSpeedRequest request) => new()
    {
        [JsonNamingPolicy.CamelCase.ConvertName(nameof(request.Speed))] = request.Speed,
    };

    public static JsonObject Arguments(LoadGameRequest request) => new()
    {
        [JsonNamingPolicy.CamelCase.ConvertName(nameof(request.SaveName))] = request.SaveName,
        [JsonNamingPolicy.CamelCase.ConvertName(nameof(request.Readiness))] = request.Readiness,
        [JsonNamingPolicy.CamelCase.ConvertName(nameof(request.TimeoutMs))] = request.TimeoutMs,
        [JsonNamingPolicy.CamelCase.ConvertName(nameof(request.IgnoreModCompatibility))] = request.IgnoreModCompatibility,
    };

    public static JsonObject Arguments(SaveGameRequest request) => new()
    {
        [JsonNamingPolicy.CamelCase.ConvertName(nameof(request.SaveName))] = request.SaveName,
    };

    /// <summary>The reply record read from a tool call's structured reply.</summary>
    public static T Reply<T>(JsonObject reply) => reply.Deserialize(Info<T>()) ?? throw new JsonException("The reply is JSON null.");

    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)HostJsonContext.Default.GetTypeInfo(typeof(T))!;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TimeSpeedReply))]
[JsonSerializable(typeof(LoadGameReply))]
internal sealed partial class HostJsonContext : JsonSerializerContext;
