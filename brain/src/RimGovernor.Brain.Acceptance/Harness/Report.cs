using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The shared JSON report every acceptance run writes: a dynamic bag of fields,
/// always including <c>passed</c>, <c>scope</c>, <c>started_at</c> and, on failure, <c>error</c>. Finalize adds the
/// timing fields (see <c>Report.Timing.cs</c>) and writes <c>result.json</c>.
/// </summary>
public sealed partial class Report
{
    /// <summary>The report field <see cref="Expect"/> appends to.</summary>
    public const string SoftFailuresKey = "soft_failures";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public Report()
        : this(new JsonObject())
    {
    }

    /// <summary>Wraps existing report data (a result.json read back).</summary>
    public Report(JsonObject data)
    {
        Data = data;
    }

    /// <summary>The report's fields.</summary>
    public JsonObject Data { get; }

    /// <summary>Sets a field; a node that already has a parent is copied. Fields are read through the typed accessors below, never by key.</summary>
    public JsonNode? this[string key]
    {
        set => Data.Set(key, value?.Parent is null ? value : value.DeepClone());
    }

    /// <summary>The error message of a failed run; empty when none was recorded.</summary>
    public string Error => Raw("error").AsString();

    /// <summary>Whether the run asked to keep its game (the <c>keep</c> field is the boolean true).</summary>
    public bool Keep => Raw("keep").AsBool() == true;

    /// <summary>The error recorded when the game failed to stop; empty when none.</summary>
    public string StopError => Raw("stop_error").AsString();

    /// <summary>The recorded <c>world</c> block; null when none.</summary>
    public JsonObject? World => Raw("world") as JsonObject;

    /// <summary>The recorded <c>package_files</c> block; null when none.</summary>
    public JsonObject? PackageFiles => Raw("package_files").AsMap();

    /// <summary>The recorded <c>authority_reacquisitions</c> block.</summary>
    public JsonNode? AuthorityReacquisitions => Raw("authority_reacquisitions");

    /// <summary>Appends a row to the <c>dismissed_letters</c> list, creating it on the first.</summary>
    public void AddDismissedLetter(JsonObject row)
    {
        if (Raw("dismissed_letters") is not JsonArray rows)
        {
            rows = [];
            Data.Set("dismissed_letters", rows);
        }

        rows.Add(row);
    }

    /// <summary>A new report for <paramref name="scope"/>: not passed, started now.</summary>
    public static Report Create(string scope, bool headless)
    {
        var report = new Report();
        report["passed"] = false;
        report["headless"] = headless;
        report["scope"] = scope;
        report[StartedAtKey] = DateTimeOffset.UtcNow.ToString("O");
        return report;
    }

    private JsonNode? Raw(string key) => Data.TryGetPropertyValue(key, out var found) ? found : null;

    /// <summary>Whether the report has the field.</summary>
    public bool Has(string key) => Data.ContainsKey(key);

    /// <summary>Whether the run passed (the <c>passed</c> field is the boolean true).</summary>
    public bool Passed => Raw("passed").AsBool() == true;

    /// <summary>
    /// Fills the timing fields and applies the budget (a passing run over its budget fails), computes the artifact
    /// hash manifest for <paramref name="output"/> and the metrics block, writes result.json, and returns the process
    /// exit code: 0 when the report passed, 1 otherwise.
    /// </summary>
    public int Finalize(string output)
    {
        FinalizeTiming(DateTimeOffset.UtcNow);
        if (EvidenceBudget.Artifacts(output, this) is { } hashes)
        {
            var manifest = new JsonObject();
            foreach (var (path, hash) in hashes)
            {
                manifest.Set(path, hash);
            }

            this["artifacts"] = manifest;
        }

        if (!Has("wait_stats"))
        {
            this["wait_stats"] = WaitStatistics.Snapshot();
        }

        if (!Has("installed_package") && RunState.InstalledPackage is { } installed)
        {
            this["installed_package"] = installed.DeepClone();
        }

        this[Metrics.MetricsKey] = Metrics.Compute(this, output).ToJson();
        Write(output);
        return Passed ? 0 : 1;
    }

    /// <summary>
    /// Records <paramref name="error"/> under <paramref name="check"/> and returns whether it was null, so a case can
    /// run several independent assertions over one finished state and see every failure in one run instead of the
    /// first. A case still returns early when a later step depends on the result; the runner fails a case whose body
    /// returned normally with failures recorded (<see cref="SoftFailure"/>).
    /// </summary>
    public bool Expect(string check, Exception? error)
    {
        if (error is null)
        {
            return true;
        }

        if (Raw(SoftFailuresKey) is not JsonArray failures)
        {
            failures = [];
            Data.Set(SoftFailuresKey, failures);
        }

        failures.Add(new JsonObject { { "check", check }, { "error", error.Message } });
        return false;
    }

    /// <summary>The failures <see cref="Expect"/> recorded, joined; null when none.</summary>
    public Exception? SoftFailure()
    {
        var errors = new List<Exception>();
        foreach (var failure in Raw(SoftFailuresKey).AsSlice())
        {
            var row = HarnessJson.TryRead(failure, HarnessJsonContext.Default.SoftFailureRow);
            errors.Add(new AcceptanceException($"{row?.Check}: {row?.Error}"));
        }

        return errors.Count switch
        {
            0 => null,
            1 => errors[0],
            _ => new AggregateException(errors),
        };
    }

    /// <summary>
    /// Writes the report as <c>output/result.json</c>. Finalize calls it; a runner that adds bookkeeping after
    /// Finalize (the series' drift flags) calls it again. A failure to write is not reported.
    /// </summary>
    public void Write(string output)
    {
        try
        {
            File.WriteAllText(Path.Combine(output, "result.json"), Marshal());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The report is evidence, not a result of the run itself.
        }
    }

    /// <summary>
    /// Encodes the report with <c>diagnosis</c> (the failure digest) as the first member so it is the first thing
    /// read; the remaining fields follow in ordinal key order.
    /// </summary>
    public string Marshal()
    {
        var ordered = new JsonObject();
        if (Data.TryGetPropertyValue("diagnosis", out var diagnosis))
        {
            ordered.Add("diagnosis", diagnosis?.DeepClone());
        }

        foreach (var (key, value) in Data.Where(pair => pair.Key != "diagnosis").OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ordered.Add(key, value?.DeepClone());
        }

        return ordered.ToJsonString(WriteOptions);
    }
}
