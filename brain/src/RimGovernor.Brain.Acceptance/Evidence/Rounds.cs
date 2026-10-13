using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Issues one HTTP request to the brain's player API and returns its decoded JSON body (Go <c>HTTPGet</c>).</summary>
public delegate Task<JsonObject> HttpGet(string method, string path, CancellationToken cancellationToken);

/// <summary>Round-supervision assertions (Go <c>rounds.go</c>).</summary>
public static class Rounds
{
    /// <summary>
    /// Asserts the supervised clock is still in automate mode: a non-automate state is always an interruption, and
    /// the player clock is fetched only to enrich that error, never to justify continuing.
    /// </summary>
    public static async Task AssertRoundsRunningAsync(HttpGet http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var state = await http("GET", "/api/state", cancellationToken).ConfigureAwait(false);
        if (state["mode"].AsString() == "automate")
        {
            return;
        }

        var clock = await http("GET", "/api/player/clock", cancellationToken).ConfigureAwait(false);
        throw new AcceptanceException($"routine acceptance interrupted: state={state.ToJson()}, clock={clock.ToJson()}");
    }
}
