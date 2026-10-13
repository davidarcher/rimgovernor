using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A failed receipt read for live errors and retained evidence (Go <c>stepresult.Failure</c>): a blocking bridge
/// attention is told apart from a native exception or refusal. <see cref="Detail"/> keeps the text blocks,
/// including attention ids and sample messages.
/// </summary>
public sealed record StepFailure(string Kind, string Summary, string Detail);

/// <summary>Interprets a failed receipt envelope (Go <c>stepresult.Parse</c>).</summary>
public static class StepResult
{
    /// <summary>
    /// Reads the result envelope, not a step's outer transport error. Successful receipts and malformed evidence are
    /// not classified as native refusals. A refusal does not have to arrive as an error: a fixture op that declines
    /// reports a successful receipt carrying <c>success: false</c> and its own reason, and a fixture whose game threw
    /// reports the exception the same way.
    /// </summary>
    public static StepFailure? Parse(JsonNode? envelope)
    {
        if (envelope is not JsonObject result)
        {
            return null;
        }

        var structured = result["structuredContent"] as JsonObject;
        var exception = structured.Member("exception").AsString();
        var attentionId = structured.Member("attentionId").AsString();
        var summary = structured.Member("summary").AsString();
        var reason = structured.Member("reason").AsString();
        var message = structured.Member("message").AsString();
        var success = structured.Member("success").AsBool();
        var refused = structured.Member("refused").AsBool() ?? false;
        var isError = result["isError"].AsBool() ?? false;

        // An exception alone is not a failure: a successful reply may report one it handled. The refusal flags are
        // what the bridge itself refuses on.
        var declined = success is false;
        if (!isError && !declined && !refused)
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var block in result["content"].AsSlice())
        {
            var text = block.Member("text").AsString();
            if (text.Trim().Length > 0)
            {
                texts.Add(text);
            }
        }

        var kind = "refusal";
        var detail = string.Join('\n', texts);
        var line = Refusals.FirstLine(detail);

        // A declined op's single text block is the whole reply JSON, which says nothing on one line; its structured
        // "reason" is the refusal itself.
        if (reason.Length > 0)
        {
            line = Refusals.FirstLine(reason);
        }

        if (line.Length == 0 && message.Length > 0)
        {
            line = Refusals.FirstLine(message);
        }

        if (exception.Length > 0)
        {
            kind = "native exception";
            line = Refusals.FirstLine(exception);
        }

        var lower = detail.ToLowerInvariant();
        if (attentionId.Length > 0 || lower.Contains("blocking attention", StringComparison.Ordinal) || lower.Contains("attentionid:", StringComparison.Ordinal) || lower.Contains("attention id:", StringComparison.Ordinal))
        {
            kind = "blocking attention";
            if (summary.Length > 0)
            {
                line = Refusals.FirstLine(summary);
            }

            foreach (var row in detail.Split('\n'))
            {
                var trimmed = row.Trim();
                if (trimmed.StartsWith("Summary:", StringComparison.Ordinal))
                {
                    line = Refusals.FirstLine(trimmed["Summary:".Length..]);
                    break;
                }
            }

            // Preserve structured-only attention metadata (including sample messages).
            if (structured is not null)
            {
                detail += "\n" + structured.ToJsonString();
            }
        }

        if (line.Length == 0)
        {
            line = "tool returned isError without a message";
        }

        return new StepFailure(kind, line, detail);
    }
}
