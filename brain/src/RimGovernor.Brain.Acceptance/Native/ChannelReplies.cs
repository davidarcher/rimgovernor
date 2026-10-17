using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

// The local HTTP channel's own replies as the transport reads them (#3410): the route list and a failure body. A record
// carries only what the transport reads; the channel writes them with Utf8JsonWriter, so there is no shared DTO.

/// <summary><c>GET /api/tools</c>: the route names <c>POST /native/&lt;name&gt;</c> answers.</summary>
internal sealed record ChannelTools
{
    public IReadOnlyList<string?>? Tools { get; init; }
}

/// <summary>A non-200 reply's body: the channel's failure code and detail, each read only when a JSON string.</summary>
internal sealed record ChannelFailure
{
    public JsonElement? Code { get; init; }

    public JsonElement? Detail { get; init; }
}

/// <summary>
/// The fields a refusal's own account may sit in (<see cref="Refusals.Cause"/>), each kept as its JSON value: only a
/// string one is a cause.
/// </summary>
internal sealed record RefusalAccount
{
    public JsonElement? Reason { get; init; }

    public JsonElement? Exception { get; init; }

    public JsonElement? Error { get; init; }

    public JsonElement? Message { get; init; }

    /// <summary>The candidates in the order <see cref="Refusals.Cause"/> tries them.</summary>
    public IEnumerable<string> Candidates() => new[] { Reason, Exception, Error, Message }.Select(ChannelJson.Text);
}

internal static class ChannelJson
{
    /// <summary>The string a JSON value holds; "" for any other value or none.</summary>
    internal static string Text(JsonElement? value) => value is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";
}

/// <summary>The channel replies' JSON: source-generated, camelCase, unknown fields ignored.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChannelTools))]
[JsonSerializable(typeof(ChannelFailure))]
[JsonSerializable(typeof(RefusalAccount))]
internal sealed partial class ChannelJsonContext : JsonSerializerContext;
