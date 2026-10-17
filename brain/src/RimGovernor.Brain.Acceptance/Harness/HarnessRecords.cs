using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RimGovernor.Brain.Acceptance;

// The harness's own records for the JSON it reads back (#3410): a recorded wire reply's outcome choice, the observation
// context a reply carries, and the summary rows the harness writes and later reads. Each mirrors only the fields its
// reader uses and ignores the rest.

/// <summary>A <c>rimgovernor/*</c> reply record's <c>outcome</c> choice, read without knowing the op.</summary>
internal sealed record RecordedReply
{
    public RecordedChoice? Outcome { get; init; }
}

/// <summary>An outcome choice: its case name and body.</summary>
internal sealed record RecordedChoice
{
    public string? Type { get; init; }

    public JsonObject? Value { get; init; }
}

/// <summary>An outcome body's observation context, read for the tick it observed.</summary>
internal sealed record ContextBody
{
    public ContextTick? Context { get; init; }
}

/// <summary>The tick of an observation context.</summary>
internal sealed record ContextTick
{
    public long? Tick { get; init; }
}

/// <summary>A reply envelope's encoded <c>payload</c> string.</summary>
internal sealed record ReplyPayload
{
    public string? Payload { get; init; }
}

/// <summary>A report's <c>game_reuse</c> block, read for the open time.</summary>
internal sealed record GameReuseRow
{
    public long? OpenMs { get; init; }
}

/// <summary>One <see cref="Report.Expect"/> failure row.</summary>
internal sealed record SoftFailureRow
{
    public string Check { get; init; } = "";

    public string Error { get; init; } = "";
}

/// <summary>A start row (what a start replies), read for the lab block a lab start carries.</summary>
internal sealed record StartRow
{
    public JsonObject? Lab { get; init; }
}

/// <summary>A tool receipt envelope as <see cref="StepResult"/> classifies it.</summary>
internal sealed record Receipt
{
    public bool? IsError { get; init; }

    public JsonObject? StructuredContent { get; init; }

    public List<ReceiptBlock>? Content { get; init; }
}

/// <summary>A receipt text block.</summary>
internal sealed record ReceiptBlock
{
    public string? Text { get; init; }
}

/// <summary>The structured fields of a receipt that name a failure.</summary>
internal sealed record ReceiptStructured
{
    public string? Exception { get; init; }

    public string? AttentionId { get; init; }

    public string? Summary { get; init; }

    public string? Reason { get; init; }

    public string? Message { get; init; }

    public bool? Success { get; init; }

    public bool? Refused { get; init; }
}

/// <summary>The <c>timeoutMs</c> a lifecycle tool's arguments ask the native side to wait.</summary>
internal sealed record NativeWaitArguments
{
    public long? TimeoutMs { get; init; }
}

/// <summary>
/// The observation-load readers' summary row (<see cref="ObservationReaders.StopAsync"/>): written into the speed
/// matrix row and read back by <see cref="ObservationReaders.ReaderProblems"/>.
/// </summary>
public sealed record ReadersSummary
{
    public int Readers { get; init; }

    public ulong Reads { get; init; }

    public ulong Errors { get; init; }

    [JsonPropertyName("duration_s")]
    public double DurationS { get; init; }

    [JsonPropertyName("read_p50_ms")]
    public double ReadP50Ms { get; init; }

    [JsonPropertyName("read_p95_ms")]
    public double ReadP95Ms { get; init; }

    [JsonPropertyName("read_max_ms")]
    public double ReadMaxMs { get; init; }
}

/// <summary>A speed matrix metrics row as <see cref="SpeedMetrics.FromRows"/> reads it; a missing number is -1.</summary>
internal sealed record SpeedMetricsRow
{
    public string Case { get; init; } = "";

    public string Speed { get; init; } = "";

    public double WallTps { get; init; } = -1;

    public double TicksAdvanced { get; init; } = -1;

    public double PausedFractionNative { get; init; } = -1;

    public double Holds { get; init; } = -1;

    [JsonPropertyName("resume_latency_ms_mean")]
    public double ResumeLatencyMsMean { get; init; } = -1;

    public double EffectiveSpeed { get; init; } = -1;
}

/// <summary>Reads harness JSON into the records above.</summary>
internal static class HarnessJson
{
    /// <summary>
    /// <paramref name="node"/> as <typeparamref name="T"/>; null for no node or a node of another shape (the readers
    /// here classify evidence and never fail on it).
    /// </summary>
    public static T? TryRead<T>(JsonNode? node, JsonTypeInfo<T> info)
        where T : class
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.Deserialize(info);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RecordedReply))]
[JsonSerializable(typeof(ContextBody))]
[JsonSerializable(typeof(NativeWaitArguments))]
[JsonSerializable(typeof(ReplyPayload))]
[JsonSerializable(typeof(GameReuseRow))]
[JsonSerializable(typeof(SoftFailureRow))]
[JsonSerializable(typeof(Receipt))]
[JsonSerializable(typeof(StartRow))]
[JsonSerializable(typeof(ReceiptStructured))]
internal sealed partial class HarnessJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ReadersSummary))]
[JsonSerializable(typeof(SpeedMetricsRow))]
internal sealed partial class HarnessRowJsonContext : JsonSerializerContext;
