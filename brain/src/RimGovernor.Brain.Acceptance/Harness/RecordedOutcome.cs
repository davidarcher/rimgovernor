using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <c>outcome</c> choice of a recorded <c>rimgovernor/*</c> reply payload, read without knowing the op: every reply
/// record carries its result as <c>{"outcome": {"type": case, "value": body}}</c>. Evidence readers (the tick tracker, the
/// post-mortem refusal scan) use it over rows of any op; a case reads a typed reply instead.
/// </summary>
internal static class RecordedOutcome
{
    /// <summary>The outcome case and body of <paramref name="payload"/>; null when it is not a JSON reply with an outcome.</summary>
    public static (string Case, JsonObject Value)? Of(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) is JsonObject { } reply
                && reply["outcome"] is JsonObject outcome
                && outcome["type"] is JsonValue type && type.TryGetValue<string>(out var name)
                && outcome["value"] is JsonObject value
                    ? (name, value)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
