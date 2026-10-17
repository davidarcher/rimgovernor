using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One row of a suite file (<c>-suite</c>, the embedded suites): a case name and the criterion it stands for.</summary>
internal sealed record SuiteFileRow
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("acceptance")]
    public string Acceptance { get; init; } = "";
}

/// <summary>A suite result.json read back as a <c>-baseline</c>: its case rows.</summary>
internal sealed record SuiteResultFile
{
    [JsonPropertyName("cases")]
    public IReadOnlyList<SuiteResultCase>? Cases { get; init; }
}

/// <summary>The fields of a suite row a baseline reads: wall and boot time and the flake record.</summary>
internal sealed record SuiteResultCase
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("wall_ms")]
    public double? WallMs { get; init; }

    [JsonPropertyName("boot_ms")]
    public double? BootMs { get; init; }

    [JsonPropertyName("flake")]
    public Flake? Flake { get; init; }
}

/// <summary>One row of <c>gh run list --json databaseId,headSha,conclusion</c>.</summary>
internal sealed record GhRunRow
{
    [JsonPropertyName("databaseId")]
    public long DatabaseId { get; init; }

    [JsonPropertyName("headSha")]
    public string HeadSha { get; init; } = "";

    [JsonPropertyName("conclusion")]
    public string Conclusion { get; init; } = "";
}

/// <summary>The suite, series and result.json readers' records: source-generated, numbers also read from strings, as Go's decode accepted.</summary>
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(CaseResult))]
[JsonSerializable(typeof(ReportTiming))]
[JsonSerializable(typeof(SeriesLine))]
[JsonSerializable(typeof(IReadOnlyList<SuiteFileRow>))]
[JsonSerializable(typeof(SuiteResultFile))]
[JsonSerializable(typeof(IReadOnlyList<GhRunRow>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(Flake))]
[JsonSerializable(typeof(DriftFlag))]
internal sealed partial class SuiteJsonContext : JsonSerializerContext;
