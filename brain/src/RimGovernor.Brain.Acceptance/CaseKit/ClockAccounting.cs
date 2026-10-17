using System.Text.Json.Nodes;

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
/// none of it: the clock status keeps only the effective tick rate the pacing reads.
/// </summary>
public sealed record ClockAccounting
{
    /// <summary>The tool's route name.</summary>
    public const string Tool = "rimgovernor/acceptance_read_clock_accounting";

    /// <summary>Wall time the supervisor's own hold/run transitions account for, on a monotonic clock (the open gap included while held).</summary>
    public ulong PausedMs { get; init; }

    public ulong RunningMs { get; init; }

    /// <summary>Main-thread wall time of the hazard probe and of the fact-change digests, and their pass counts.</summary>
    public double ProbeMs { get; init; }

    public double DigestMs { get; init; }

    public ulong Probes { get; init; }

    public ulong Digests { get; init; }

    /// <summary>The widest tick gap between consecutive probes in the session.</summary>
    public long SessionMaxProbeTickGap { get; init; }

    /// <summary>Per hazard class: the declared bound beside the widest gap observed.</summary>
    public IReadOnlyList<HazardGap> HazardGaps { get; init; } = [];

    /// <summary>The frame account: frames that ticked, those whose tick work exceeded the frame budget, and the widest frame's tick work.</summary>
    public ulong PacedFrames { get; init; }

    public ulong OverBudget { get; init; }

    public double MaxFrameMs { get; init; }

    /// <summary>Reads the accounting now.</summary>
    public static async Task<ClockAccounting> ReadAsync(Harness harness, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var reply = await harness.CallAsync(label, Tool, null, cancellationToken).ConfigureAwait(false);
        if (reply["payload"] is not JsonValue value || !value.TryGetValue<string>(out var payload))
        {
            throw new AcceptanceException($"{Tool}: reply carried no payload: {reply.ToJsonString()}");
        }

        try
        {
            return Parse(JsonNode.Parse(payload) as JsonObject ?? throw new AcceptanceException($"{Tool}: payload is not an object"));
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new AcceptanceException($"{Tool}: invalid payload: {e.Message}", e);
        }
    }

    /// <summary>The accounting out of the tool's payload.</summary>
    public static ClockAccounting Parse(JsonObject read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var gaps = new List<HazardGap>();
        foreach (var raw in read["hazardGaps"] as JsonArray ?? [])
        {
            if (raw is JsonObject gap)
            {
                gaps.Add(new HazardGap
                {
                    HazardClass = gap["hazardClass"]?.ToString() ?? "",
                    BoundTicks = (long)Number(gap, "boundTicks"),
                    MaxTickGap = (long)Number(gap, "maxTickGap"),
                    Hooked = gap["hooked"] is JsonValue hooked && hooked.TryGetValue<bool>(out var h) && h,
                });
            }
        }

        return new ClockAccounting
        {
            PausedMs = Count(read, "pausedMs"),
            RunningMs = Count(read, "runningMs"),
            ProbeMs = Number(read, "probeElapsedMs"),
            DigestMs = Number(read, "digestElapsedMs"),
            Probes = Count(read, "probeTotal"),
            Digests = Count(read, "digestTotal"),
            SessionMaxProbeTickGap = (long)Number(read, "sessionMaxProbeTickGap"),
            HazardGaps = gaps,
            PacedFrames = Count(read, "pacedFrames"),
            OverBudget = Count(read, "pacedFramesOverBudget"),
            MaxFrameMs = Number(read, "maxPacedFrameMs"),
        };
    }

    private static double Number(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static ulong Count(JsonObject o, string name) => Number(o, name) is var n and > 0 ? (ulong)n : 0;
}
