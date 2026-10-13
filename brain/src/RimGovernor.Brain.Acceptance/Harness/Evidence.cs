using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Every native call a <see cref="Harness"/> makes lands as one evidence row, <c>&lt;output&gt;/NNNN-&lt;label&gt;.json</c>,
/// holding the request and the reply plus when it was observed: <c>sequence</c> (NNNN, allocated per output
/// directory for the whole process so the harnesses a case opens over one game number one stream),
/// <c>observed_at</c> (the millisecond wall time the call was sent), <c>elapsed_ms</c> (until the reply) and
/// <c>tick</c> (the game tick the reply carries, when it does).
/// </summary>
public static class Evidence
{
    private static readonly ConcurrentDictionary<string, int> Sequences = new(StringComparer.Ordinal);

    /// <summary>Allocates the next NNNN for <paramref name="output"/>.</summary>
    public static int NextSequence(string output) =>
        Sequences.AddOrUpdate(Path.GetFullPath(output), 1, (_, current) => current + 1);

    /// <summary>The row's file for a sequence and label under <paramref name="output"/>.</summary>
    public static string PathFor(string output, int sequence, string label) =>
        Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"{sequence:0000}-{label}.json"));

    /// <summary>Builds the row for one call: the request, the reply envelope (or the error and whatever envelope came with it) and the observation stamps.</summary>
    public static JsonObject Row(int sequence, string tool, JsonObject arguments, DateTimeOffset sent, TimeSpan elapsed, JsonNode? envelope, Exception? error, ulong? tick)
    {
        var row = new JsonObject
        {
            ["sequence"] = sequence,
            ["observed_at"] = sent.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["elapsed_ms"] = (long)elapsed.TotalMilliseconds,
            ["request"] = new JsonObject { ["tool"] = tool, ["arguments"] = arguments.DeepClone() },
        };
        if (error is not null)
        {
            row["error"] = error.Message;
        }

        if (envelope is not null)
        {
            row["result"] = envelope.DeepClone();
        }

        if (tick is { } observed)
        {
            row["tick"] = (long)observed;
        }

        return row;
    }
}
