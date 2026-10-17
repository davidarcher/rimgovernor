using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RimGovernor.Contracts.Host;

// The host-level tools the acceptance harness calls by name outside the wire ops (#3410): the RimGovernor host's
// rimgovernor/set_time_speed and rimgovernor/load_game_ready. A reply record carries only what a caller reads; the host
// replies with more, which the reader ignores.

/// <summary><c>rimgovernor/set_time_speed</c>'s arguments.</summary>
public sealed record TimeSpeedRequest
{
    /// <summary>Paused, Normal, Fast, Superfast or Ultrafast.</summary>
    public required string Speed { get; init; }

    /// <summary>Sets TickManager.UltraSpeedBoost; null keeps the current value.</summary>
    public bool? UltraSpeedBoost { get; init; }

    /// <summary>Pauses the clock and clears the boost.</summary>
    public static TimeSpeedRequest Paused { get; } = new() { Speed = "Paused", UltraSpeedBoost = false };
}

/// <summary><c>rimgovernor/set_time_speed</c>'s reply.</summary>
public sealed record TimeSpeedReply
{
    public bool Success { get; init; }

    /// <summary>The clock speed after the call.</summary>
    public string? TimeSpeed { get; init; }

    public bool Paused { get; init; }
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

/// <summary><c>rimgovernor/load_game_ready</c>'s reply.</summary>
public sealed record LoadGameReply
{
    public bool Success { get; init; }

    /// <summary>The sanitized save name the host loaded.</summary>
    public string? SaveName { get; init; }

    public string? Message { get; init; }
}

/// <summary>The one JSON entry point for the host-tool records: source-generated, camelCase, unknown reply fields ignored.</summary>
public static class HostJson
{
    /// <summary>The request as the JSON arguments object a tool call sends.</summary>
    public static JsonObject Arguments<T>(T request) =>
        JsonSerializer.SerializeToNode(request, Info<T>()) as JsonObject ?? throw new JsonException("A host-tool request is not a JSON object.");

    /// <summary>The reply record read from a tool call's structured reply.</summary>
    public static T Reply<T>(JsonObject reply) => reply.Deserialize(Info<T>()) ?? throw new JsonException("The reply is JSON null.");

    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)HostJsonContext.Default.GetTypeInfo(typeof(T))!;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TimeSpeedRequest))]
[JsonSerializable(typeof(TimeSpeedReply))]
[JsonSerializable(typeof(LoadGameRequest))]
[JsonSerializable(typeof(LoadGameReply))]
[JsonSerializable(typeof(SaveGameRequest))]
internal sealed partial class HostJsonContext : JsonSerializerContext;
