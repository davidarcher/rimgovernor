using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Host;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The host-level tool calls (clock speed and save load) over their Contracts records.</summary>
public sealed partial class Harness
{
    /// <summary>The host tool that sets the clock speed.</summary>
    public const string SetTimeSpeedTool = "rimgovernor/set_time_speed";

    /// <summary>The host tool that loads a save and waits for the requested readiness.</summary>
    public const string LoadGameReadyTool = "rimgovernor/load_game_ready";

    /// <summary>Sets the clock speed through <see cref="SetTimeSpeedTool"/>, recorded under <paramref name="label"/>.</summary>
    public async Task<TimeSpeedReply> SetTimeSpeedAsync(string label, TimeSpeedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await CallAsync(label, SetTimeSpeedTool, HostJson.Arguments(request), cancellationToken).ConfigureAwait(false);
        return HostReply<TimeSpeedReply>(label, SetTimeSpeedTool, reply);
    }

    /// <summary>Pauses the clock and clears the speed boost (<see cref="TimeSpeedRequest.Paused"/>).</summary>
    public Task<TimeSpeedReply> PauseClockAsync(string label, CancellationToken cancellationToken) =>
        SetTimeSpeedAsync(label, TimeSpeedRequest.Paused, cancellationToken);

    /// <summary>The native load wait a request without its own <see cref="LoadGameRequest.TimeoutMs"/> gets.</summary>
    public static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(90);

    /// <summary>Loads a save through <see cref="LoadGameReadyTool"/>, recorded under <paramref name="label"/>; a zero timeout is <see cref="LoadWait"/>.</summary>
    public async Task<LoadGameReply> LoadGameReadyAsync(string label, LoadGameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TimeoutMs <= 0)
        {
            request = request with { TimeoutMs = (long)LoadWait.TotalMilliseconds };
        }

        var reply = await CallAsync(label, LoadGameReadyTool, HostJson.Arguments(request), cancellationToken).ConfigureAwait(false);
        return HostReply<LoadGameReply>(label, LoadGameReadyTool, reply);
    }

    private static T HostReply<T>(string label, string tool, JsonObject reply)
    {
        try
        {
            return HostJson.Reply<T>(reply);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{label}: {tool}: invalid reply: {e.Message}", e);
        }
    }
}
