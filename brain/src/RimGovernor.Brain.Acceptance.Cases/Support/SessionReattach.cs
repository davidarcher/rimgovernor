using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The session hand-back both the upkeep and defense campaigns make between service phases.</summary>
internal static class SessionReattach
{
    /// <summary>
    /// Takes the session back after a service stop and pauses the clock: every fixture op needs a paused map, and the
    /// service leaves the game running (Go: the reattached session paused first).
    /// </summary>
    internal static async Task<Harness> PausedAsync(ISession s, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(s);
        var h = s.Reattach();
        _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        return h;
    }
}
