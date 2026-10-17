using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Reply assertions the read cases share (Go <c>nativeaccept</c> <c>RequireIdentifier</c>, <c>RequireSnapshot</c>,
/// <c>RequireIssueReason</c>, <c>FailureCode</c> and <c>UnavailableReason</c>): each returns a verdict or throws an
/// <see cref="AcceptanceException"/> naming what was missing.
/// </summary>
public static class NativeAssert
{
    private const int MaxIdentifierBytes = 256;

    /// <summary>
    /// Asserts <paramref name="value"/> is a populated, non-whitespace-only identifier string with no embedded null
    /// character and at most 256 UTF-8 bytes. Throws otherwise.
    /// </summary>
    public static void RequireIdentifier(JsonNode? value, string what = "")
    {
        var prefix = what.Length == 0 ? "" : what + ": ";
        if (value is not JsonValue json || !json.TryGetValue<string>(out var text))
        {
            throw new AcceptanceException($"{prefix}expected an identifier string, found {value.ToJson()}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AcceptanceException($"{prefix}identifier is empty or whitespace-only: {value.ToJson()}");
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new AcceptanceException($"{prefix}identifier contains a null byte: {value.ToJson()}");
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxIdentifierBytes)
        {
            throw new AcceptanceException($"{prefix}identifier exceeds 256 UTF-8 bytes: {value.ToJson()}");
        }
    }

    /// <summary>
    /// Asserts <paramref name="value"/> is a populated SnapshotRef {context, entityId, token}: a non-empty CAS token
    /// proving the row can be used as an operation EntityPrecondition. It does not require the context to match a
    /// caller-supplied one: some rows reference a snapshot in their own right.
    /// </summary>
    public static void RequireSnapshot(JsonNode? value)
    {
        if (value.AsMap() is not { } snapshot)
        {
            throw new AcceptanceException($"expected a populated CAS snapshot object, found {value.ToJson()}");
        }

        if (snapshot["context"] is not JsonObject)
        {
            throw new AcceptanceException($"snapshot missing context: {snapshot.ToJson()}");
        }

        RequireIdentifier(snapshot["entityId"], "snapshot entityId");
        RequireIdentifier(snapshot["token"], "snapshot token");
    }

    /// <summary>Whether <paramref name="issues"/> holds a row for <paramref name="field"/> whose unavailable reason is <paramref name="reason"/>.</summary>
    public static bool RequireIssueReason(IEnumerable<JsonNode?> issues, string field, string reason)
    {
        ArgumentNullException.ThrowIfNull(issues);
        foreach (var raw in issues)
        {
            if (raw.AsMap() is not { } issue || issue["field"].AsString() != field)
            {
                continue;
            }

            if (issue["unavailable"].AsMap() is { } unavailable && unavailable["reason"].AsString() == reason)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The failure code of a reply carrying a failure, null for any other reply.</summary>
    public static string? FailureCode(JsonObject reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return reply["failure"].AsMap() is { } failure ? failure["code"].AsString() : null;
    }

    /// <summary>The unavailable reason of a reply carrying an unavailable member, null for any other reply.</summary>
    public static string? UnavailableReason(JsonObject reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return reply["unavailable"].AsMap() is { } unavailable ? unavailable["reason"].AsString() : null;
    }
}
