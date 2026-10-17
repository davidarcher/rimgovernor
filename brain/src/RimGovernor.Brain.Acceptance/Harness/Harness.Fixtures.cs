using System.Text.Json;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The fixture-op replies the lifecycle reads, as their Fixtures.Contracts records.</summary>
public sealed partial class Harness
{
    /// <summary>
    /// <paramref name="reply"/>, the structured reply of fixture op <paramref name="tool"/>, as its Fixtures.Contracts
    /// record; a reply of another shape fails with the reply.
    /// </summary>
    internal static T FixtureReply<T>(string tool, JsonObject reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        try
        {
            return Fx.FixtureJson.Deserialize<T>(reply.ToJsonString());
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{tool}: unexpected reply {reply.ToJsonString()}: {e.Message}", e);
        }
    }
}
