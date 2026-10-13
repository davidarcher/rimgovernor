using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

public sealed partial class Harness
{
    /// <summary>Only an explicit refusal proves that retrying cannot repeat an executed order.</summary>
    internal static bool BlockingAttention(Exception? error) =>
        error is NativeRefusedException refusal && refusal.Result.Structured.Member("status").AsString() == "blocked_by_attention";

    /// <summary>
    /// Reads the game's blocking attention and acknowledges it. Attention receipts are evidence, not a whitelist of
    /// harmless exceptions: every sample and severity is kept even when acknowledgement lets the case continue.
    /// <paramref name="record"/> is called with the partial row as soon as the attention was read.
    /// </summary>
    internal async Task<(JsonObject Row, Exception? Error)> ReadAndAckAttentionAsync(Action<JsonObject> record, CancellationToken cancellationToken)
    {
        var row = new JsonObject();
        NativeResult? result;
        try
        {
            result = await Transport.GetAttentionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            row["error"] = e.Message;
            return (row, new AcceptanceException("games_get_attention: " + e.Message, e));
        }

        if (result is null)
        {
            var unsupported = new AcceptanceException("games_get_attention: this transport has no attention channel");
            row["error"] = unsupported.Message;
            record(row);
            return (row, unsupported);
        }

        row["result"] = result.Envelope?.DeepClone();
        record(row);
        var attention = result.Structured.Member("attention").AsMap();
        if (attention is null)
        {
            return (row, null);
        }

        var id = attention["attentionId"].AsString();
        if (id.Length == 0)
        {
            return Fail(row, "games_get_attention: attention missing attentionId");
        }

        try
        {
            var ack = await Transport.AckAttentionAsync(id, cancellationToken).ConfigureAwait(false);
            row["acknowledgement"] = ack?.Envelope?.DeepClone();
        }
        catch (AcceptanceException e)
        {
            return Fail(row, "games_ack_attention: " + e.Message);
        }

        row["acknowledged"] = true;
        return (row, null);
    }

    private static (JsonObject Row, Exception? Error) Fail(JsonObject row, string message)
    {
        row["error"] = message;
        return (row, new AcceptanceException(message));
    }
}

public sealed partial class Report
{
    /// <summary>
    /// Aggregates the attentions the evidence steps retained so reports also keep them from short-lived or standalone
    /// harnesses, including a run that failed during session startup.
    /// </summary>
    internal void CollectAttentions(string output)
    {
        if (!Directory.Exists(output))
        {
            return;
        }

        var attentions = new JsonArray();
        foreach (var path in Directory.EnumerateFiles(output, "*-*.json").Where(IsStepFile).Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject step && step["attention"] is JsonObject attention)
                {
                    attention["step"] = Path.GetFileName(path);
                    attentions.Add(attention.DeepClone());
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable evidence step carries no attention.
            }
        }

        if (attentions.Count > 0)
        {
            this["attentions"] = attentions;
        }
    }

    private static bool IsStepFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 0 && char.IsAsciiDigit(name[0]);
    }
}
