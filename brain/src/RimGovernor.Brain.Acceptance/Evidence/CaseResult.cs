using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The result.json fields the series, the suite and the repeat summary read back (#3410): a typed view over a report (a
/// run's own, or a child's result.json), instead of string-keyed reads. Blocks copied on verbatim stay JSON nodes.
/// </summary>
internal sealed record CaseResult
{
    [JsonPropertyName("passed")]
    public bool? Passed { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("game_reuse")]
    public JsonNode? GameReuse { get; init; }

    [JsonPropertyName(Report.BootMsKey)]
    public double? BootMs { get; init; }

    /// <summary>The flat metrics block, read into a Metrics by its numbers.</summary>
    [JsonPropertyName("metrics")]
    public JsonObject? Metrics { get; init; }

    [JsonPropertyName("world")]
    public JsonObject? World { get; init; }

    [JsonPropertyName("flake")]
    public Flake? Flake { get; init; }

    [JsonPropertyName("drift")]
    public IReadOnlyList<DriftFlag>? Drift { get; init; }

    [JsonPropertyName("start")]
    public JsonNode? Start { get; init; }

    /// <summary>The view of <paramref name="report"/>'s fields.</summary>
    public static CaseResult Of(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Of(report.Data);
    }

    /// <summary>The view of a result.json object; a field of another kind than its writer writes throws <see cref="JsonException"/>.</summary>
    public static CaseResult Of(JsonObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Deserialize(SuiteJsonContext.Default.CaseResult) ?? new CaseResult();
    }
}

/// <summary>
/// The timing and wait fields of a report the metrics block derives from: Finalize computes it from the report as the
/// run left it, so the view reads nothing a case may have written under its own keys.
/// </summary>
internal sealed record ReportTiming
{
    [JsonPropertyName(Report.WallMsKey)]
    public double? WallMs { get; init; }

    [JsonPropertyName(Report.BootMsKey)]
    public double? BootMs { get; init; }

    [JsonPropertyName(Report.TicksAdvancedKey)]
    public double? TicksAdvanced { get; init; }

    [JsonPropertyName(Report.WallTpsKey)]
    public double? WallTps { get; init; }

    [JsonPropertyName("wait_stats")]
    public WaitStats? WaitStats { get; init; }

    public static ReportTiming Of(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Data.Deserialize(SuiteJsonContext.Default.ReportTiming) ?? new ReportTiming();
    }
}

/// <summary>The counts of a report's <c>wait_stats</c> block the metrics block reads (<see cref="WaitStatistics.Snapshot"/> writes it).</summary>
internal sealed record WaitStats
{
    [JsonPropertyName("waits")]
    public double Waits { get; init; }

    [JsonPropertyName("stalled")]
    public double Stalled { get; init; }

    [JsonPropertyName("max_quiet_ms")]
    public double MaxQuietMs { get; init; }
}
