using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RimGovernor.Brain.Acceptance;

// The harness's own records for the service /api replies it reads (#3410). The player API's DTOs stay internal to
// PlayerApi; these mirror only the fields a harness reader uses, under the same camelCase names, and ignore the rest.

/// <summary><c>GET /api/state</c>: the controller's published view.</summary>
public sealed record ServiceState
{
    public bool Connected { get; init; }

    /// <summary>"automate" while the brain runs the game.</summary>
    public string Mode { get; init; } = "";

    public ServiceStatus? Status { get; init; }

    public ServiceGame? Game { get; init; }

    public ServiceGeneration? Generation { get; init; }

    public ServiceIdentity? Identity { get; init; }

    public string? ActivePlanId { get; init; }
}

/// <summary><c>/api/state</c>'s status line: what the controller is doing or waiting for.</summary>
public sealed record ServiceStatus
{
    public string Label { get; init; } = "";
}

/// <summary><c>/api/state</c>'s game read: an unknown tick is null, a stale read is one the brain has not refreshed.</summary>
public sealed record ServiceGame
{
    public long? Tick { get; init; }

    public bool? Paused { get; init; }

    public bool Stale { get; init; }
}

/// <summary><c>/api/state</c>'s generation counters (64-bit counters arrive as strings).</summary>
public sealed record ServiceGeneration
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong Revision { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong Native { get; init; }

    /// <summary>The world's root plan authority runs under.</summary>
    public string Plan { get; init; } = "";
}

/// <summary>The colony, load and map the service is attached to.</summary>
public sealed record ServiceIdentity
{
    public string ColonyId { get; init; } = "";

    public int MapId { get; init; }

    public string LoadToken { get; init; } = "";
}

/// <summary><c>GET /api/health</c>: which process answers.</summary>
public sealed record ServiceHealth
{
    public string Service { get; init; } = "";

    public string Backend { get; init; } = "";

    public int Pid { get; init; }
}

/// <summary><c>GET /api/player/session</c>.</summary>
public sealed record PlayerSession
{
    public string Mode { get; init; } = "";
}

/// <summary><c>GET /api/player/colony</c>'s census facts a sustained watch folds (an unknown fact is null).</summary>
public sealed record PlayerColony
{
    public long? Colonists { get; init; }

    public double? FoodNutrition { get; init; }

    public double? FoodRunwayDays { get; init; }

    public int? Downed { get; init; }

    public double? MoodMean { get; init; }
}

/// <summary><c>POST /api/player/control/resume</c> and <c>/pause</c>: the control record and the player state after it.</summary>
public sealed record ServiceControl
{
    public ServiceControlRecord? Record { get; init; }

    public ServiceControlState? State { get; init; }
}

/// <summary>A control request's record: its phase and the native generation a running grant holds.</summary>
public sealed record ServiceControlRecord
{
    /// <summary>"running" once a resume holds authority.</summary>
    public string Phase { get; init; } = "";

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong NativeGeneration { get; init; }
}

/// <summary>The player state a control reply carries; the generation is null while no observation is known.</summary>
public sealed record ServiceControlState
{
    public ServiceGeneration? Generation { get; init; }
}

/// <summary><c>GET /api/routines</c>: the routine families the brain was composed with.</summary>
public sealed record ServiceRoutines
{
    public IReadOnlyList<string> ActiveFamilies { get; init; } = [];
}

/// <summary>Reads a service reply body into its harness record.</summary>
public static class ServiceJson
{
    /// <summary>The <c>/api/state</c> record.</summary>
    public static ServiceState? State(JsonObject? body) => Read(body, ServiceJsonContext.Default.ServiceState, "/api/state");

    /// <summary>The <c>/api/health</c> record.</summary>
    public static ServiceHealth? Health(JsonObject? body) => Read(body, ServiceJsonContext.Default.ServiceHealth, "/api/health");

    /// <summary>The <c>/api/player/session</c> record.</summary>
    public static PlayerSession? Session(JsonObject? body) => Read(body, ServiceJsonContext.Default.PlayerSession, "/api/player/session");

    /// <summary>The <c>/api/player/colony</c> census record.</summary>
    public static PlayerColony? Colony(JsonObject? body) => Read(body, ServiceJsonContext.Default.PlayerColony, "/api/player/colony");

    /// <summary>The <c>/api/player/control/*</c> record.</summary>
    public static ServiceControl? Control(JsonObject? body) => Read(body, ServiceJsonContext.Default.ServiceControl, "/api/player/control");

    /// <summary>The <c>/api/routines</c> record.</summary>
    public static ServiceRoutines? Routines(JsonObject? body) => Read(body, ServiceJsonContext.Default.ServiceRoutines, "/api/routines");

    /// <summary>The body as <typeparamref name="T"/>; null for no body; a body of another shape throws <see cref="AcceptanceException"/>.</summary>
    public static T? Read<T>(JsonObject? body, JsonTypeInfo<T> info, string route)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(info);
        if (body is null)
        {
            return null;
        }

        try
        {
            return body.Deserialize(info);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{route}: unexpected reply {body.ToJsonString()}: {e.Message}", e);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServiceState))]
[JsonSerializable(typeof(ServiceHealth))]
[JsonSerializable(typeof(PlayerSession))]
[JsonSerializable(typeof(PlayerColony))]
[JsonSerializable(typeof(ServiceControl))]
[JsonSerializable(typeof(ServiceRoutines))]
internal sealed partial class ServiceJsonContext : JsonSerializerContext;
