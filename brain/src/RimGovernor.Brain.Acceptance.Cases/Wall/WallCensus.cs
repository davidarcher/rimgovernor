using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The legacy observation-reply readers the remaining untyped <c>pawn/*</c> cases still share; the typed wall cases no longer use them.</summary>
internal static class WallCensus
{
    /// <summary>The reply's <c>failure.code</c>, null when the reply is not a failure.</summary>
    internal static string? FailureCode(JsonObject reply) => reply["failure"].AsMap() is { } failure ? failure["code"].AsString() : null;

    /// <summary>The <c>observed</c> body of an observation reply, or an exception naming the label and the outcome found.</summary>
    internal static JsonObject Observed(string label, JsonObject reply)
    {
        try
        {
            return Harness.Outcome(reply, "observed").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }
    }
}
