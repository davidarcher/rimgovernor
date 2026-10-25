using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Host;

/// <summary><c>rimgovernor/set_time_speed</c>'s and <c>rimgovernor/press_time_speed</c>'s reply.</summary>
public sealed record TimeSpeedReply
{
    public bool Success { get; init; }

    /// <summary>The clock speed after the call.</summary>
    public string? TimeSpeed { get; init; }

    public bool Paused { get; init; }
}

/// <summary><c>rimgovernor/load_game_ready</c>'s reply.</summary>
public sealed record LoadGameReply
{
    public bool Success { get; init; }

    /// <summary>The sanitized save name the host loaded.</summary>
    public string? SaveName { get; init; }

    public string? Message { get; init; }
}
