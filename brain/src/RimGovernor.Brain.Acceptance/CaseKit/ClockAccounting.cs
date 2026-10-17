using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One hazard class the supervisor probe covers: its declared detection bound in ticks (holds at every speed), the widest
/// gap observed this session, and whether a direct game hook requests a probe on the next tick (the gap then measures hook
/// to probe).
/// </summary>
public sealed record HazardGap
{
    public string HazardClass { get; init; } = "";

    public long BoundTicks { get; init; }

    public long MaxTickGap { get; init; }

    public bool Hooked { get; init; }
}

/// <summary>
/// The clock's acceptance-only accounting (#3416), read through the mod's <c>rimgovernor/acceptance_read_clock_accounting</c>
/// tool; every counter is cumulative for the loaded game session and resets when the loaded game changes. Production reads
/// none of it: the clock status keeps only the effective tick rate the pacing reads. The members carry the payload's
/// camelCase names.
/// </summary>
public sealed record ClockAccounting
{
    /// <summary>The tool's route name.</summary>
    public const string Tool = "rimgovernor/acceptance_read_clock_accounting";

    /// <summary>Wall time the supervisor's own hold/run transitions account for, on a monotonic clock (the open gap included while held).</summary>
    public ulong PausedMs { get; init; }

    public ulong RunningMs { get; init; }

    /// <summary>Main-thread wall time of the hazard probe and of the fact-change digests, and their pass counts.</summary>
    [JsonPropertyName("probeElapsedMs")]
    public double ProbeMs { get; init; }

    [JsonPropertyName("digestElapsedMs")]
    public double DigestMs { get; init; }

    [JsonPropertyName("probeTotal")]
    public ulong Probes { get; init; }

    [JsonPropertyName("digestTotal")]
    public ulong Digests { get; init; }

    /// <summary>The widest tick gap between consecutive probes in the session.</summary>
    public long SessionMaxProbeTickGap { get; init; }

    /// <summary>Per hazard class: the declared bound beside the widest gap observed.</summary>
    public IReadOnlyList<HazardGap> HazardGaps { get; init; } = [];

    /// <summary>The frame account: frames that ticked, those whose tick work exceeded the frame budget, and the widest frame's tick work.</summary>
    public ulong PacedFrames { get; init; }

    [JsonPropertyName("pacedFramesOverBudget")]
    public ulong OverBudget { get; init; }

    [JsonPropertyName("maxPacedFrameMs")]
    public double MaxFrameMs { get; init; }

    /// <summary>Reads the accounting now.</summary>
    public static async Task<ClockAccounting> ReadAsync(Harness harness, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var reply = await harness.CallAsync(label, Tool, null, cancellationToken).ConfigureAwait(false);
        try
        {
            if (reply.Deserialize(ClockAccountingJsonContext.Default.AcceptanceReadReply)?.Payload is not { } payload)
            {
                throw new AcceptanceException($"{Tool}: reply carried no payload: {reply.ToJsonString()}");
            }

            return JsonSerializer.Deserialize(payload, ClockAccountingJsonContext.Default.ClockAccounting)
                ?? throw new AcceptanceException($"{Tool}: payload is not an object");
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{Tool}: invalid payload: {e.Message}", e);
        }
    }
}

/// <summary>An acceptance read's reply: the op's JSON as a string under <c>payload</c>.</summary>
internal sealed record AcceptanceReadReply(string? Payload);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AcceptanceReadReply))]
[JsonSerializable(typeof(ClockAccounting))]
internal sealed partial class ClockAccountingJsonContext : JsonSerializerContext;
