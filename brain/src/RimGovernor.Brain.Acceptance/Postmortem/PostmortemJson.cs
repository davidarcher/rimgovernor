using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

// The records the postmortem reads the case's own evidence through (#3410): the report fields it names, a recorded
// native call's request and result, and a recorded /api/routines reply's extent view. Each mirrors only the fields a
// digest section reads, under the evidence's own names, and ignores the rest.

/// <summary>The report fields the digest names (result.json, or the live <see cref="Report.Data"/>).</summary>
public sealed record ReportFacts
{
    public string? Case { get; init; }

    public string? Error { get; init; }

    public InstalledPackageFacts? InstalledPackage { get; init; }

    /// <summary>The facts of <paramref name="report"/>; a report of another shape reads as none.</summary>
    public static ReportFacts Of(JsonObject report)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            return report.Deserialize(PostmortemJsonContext.Default.ReportFacts) ?? new ReportFacts();
        }
        catch (JsonException)
        {
            return new ReportFacts();
        }
    }
}

/// <summary>The report's <c>installed_package</c> manifest: the revision the run binary was built from.</summary>
public sealed record InstalledPackageFacts
{
    public string? SourceRevision { get; init; }

    public bool SourceDirty { get; init; }
}

/// <summary>One recorded native call (<c>NNNN-&lt;label&gt;.json</c>, <see cref="Evidence.Row"/>): the tool asked and the result envelope.</summary>
internal sealed record RecordedStep
{
    public RecordedRequest? Request { get; init; }

    public JsonNode? Result { get; init; }
}

internal sealed record RecordedRequest
{
    public string? Tool { get; init; }
}

/// <summary>One recorded service HTTP exchange (<c>service*/http-NNNN.json</c>) whose reply is a <c>/api/routines</c> body.</summary>
internal sealed record RecordedRoutines
{
    public string? Path { get; init; }

    public int Status { get; init; }

    public RoutinesReply? Response { get; init; }
}

/// <summary>The <c>/api/routines</c> reply's extent eligibility view (camelCase, as the player API writes it).</summary>
internal sealed record RoutinesReply
{
    [JsonPropertyName("extentEligibility")]
    public ExtentView? ExtentEligibility { get; init; }
}

internal sealed record ExtentView
{
    [JsonPropertyName("known")]
    public bool Known { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("regions")]
    public IReadOnlyList<ExtentRegion> Regions { get; init; } = [];
}

internal sealed record ExtentRegion
{
    [JsonPropertyName("region")]
    public int Region { get; init; }

    [JsonPropertyName("stage")]
    public string Stage { get; init; } = "";

    [JsonPropertyName("origins")]
    public IReadOnlyList<string> Origins { get; init; } = [];

    [JsonPropertyName("facilities")]
    public IReadOnlyList<string> Facilities { get; init; } = [];

    [JsonPropertyName("activeFacilities")]
    public IReadOnlyList<string> ActiveFacilities { get; init; } = [];

    [JsonPropertyName("eligible")]
    public bool Eligible { get; init; }

    [JsonPropertyName("holdReasons")]
    public IReadOnlyList<string> HoldReasons { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ReportFacts))]
[JsonSerializable(typeof(RecordedStep))]
[JsonSerializable(typeof(RecordedRoutines))]
internal sealed partial class PostmortemJsonContext : JsonSerializerContext;
