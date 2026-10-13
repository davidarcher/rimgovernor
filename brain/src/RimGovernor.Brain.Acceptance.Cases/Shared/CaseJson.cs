using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The small decoded-JSON conveniences the ported case bodies share (Go: the per-package <c>num</c>/<c>boolean</c>/
/// <c>fmt.Sprint</c> helpers). The harness's <see cref="Dyn"/> navigation does the rest.
/// </summary>
internal static class CaseJson
{
    /// <summary>A sheet or census number; an absent field is zero, not <see cref="Dyn.AsNumber"/>'s -1.</summary>
    internal static double Num(JsonNode? value) => value is null ? 0 : value.AsNumber();

    /// <summary>A boolean field; anything that is not the boolean true is false.</summary>
    internal static bool Flag(JsonNode? value) => value.AsBool() == true;

    /// <summary>A copy of <paramref name="value"/> that can be placed under another node.</summary>
    internal static JsonNode? Copy(JsonNode? value) => value?.DeepClone();

    /// <summary>The text of an id-like value (Go <c>fmt.Sprint</c>): a string as it is, anything else as compact JSON.</summary>
    internal static string Text(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) ? text : value is null ? "" : value.ToJsonString();

    /// <summary>The ids of a JSON array, each as <see cref="Text"/>.</summary>
    internal static List<string> Texts(JsonNode? value) => [.. value.AsSlice().Select(Text)];

    /// <summary>The rows of a JSON array, each as an object (empty for anything else).</summary>
    internal static List<JsonObject> Rows(JsonNode? value) => [.. value.AsSlice().Select(row => row.AsMap() ?? [])];

    /// <summary>
    /// The single result of an <c>operations_apply</c> reply that carried one action; a missing or extra result is the
    /// case's failure, with the reply in the message.
    /// </summary>
    internal static JsonObject OneResult(string label, JsonObject reply)
    {
        var results = reply["results"].AsSlice();
        if (results.Count != 1)
        {
            throw new AcceptanceException($"{label}: expected one result, got {reply.ToJson()}");
        }

        return results[0].AsMap() ?? [];
    }

    /// <summary>The identity-scoped request every <c>observations_*</c> read starts from.</summary>
    internal static JsonObject Scoped(JsonObject identity) => new() { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } };

    /// <summary><see cref="Scoped"/> with <paramref name="extra"/>'s members added (copied) to the request.</summary>
    internal static JsonObject Scoped(JsonObject identity, JsonObject extra)
    {
        var request = Scoped(identity);
        foreach (var (key, value) in extra)
        {
            request[key] = value?.DeepClone();
        }

        return request;
    }
}
