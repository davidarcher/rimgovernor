using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The shared JSON report every acceptance run writes (Go <c>nativeaccept.Report</c>): a dynamic bag of fields,
/// always including <c>passed</c>, <c>scope</c>, <c>started_at</c> and, on failure, <c>error</c>. Finalize adds the
/// timing fields (see <c>Report.Timing.cs</c>) and writes <c>result.json</c>.
/// </summary>
public sealed partial class Report
{
    /// <summary>The report field <see cref="Expect"/> appends to.</summary>
    public const string SoftFailuresKey = "soft_failures";

    /// <summary>The exit code of a run paused at a breakpoint: the report carries <c>break</c>, neither <c>passed</c> nor <c>error</c>.</summary>
    public const int ExitBreak = 3;

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

    /// <summary>A field; setting one moves a node that already has a parent by copying it.</summary>
    public JsonNode? this[string key]
    {
        get => Data[key];
        set => Data[key] = value?.Parent is null ? value : value.DeepClone();
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

    /// <summary>Whether the report has the field.</summary>
    public bool Has(string key) => Data.ContainsKey(key);

    /// <summary>Whether the run passed (the <c>passed</c> field is the boolean true).</summary>
    public bool Passed => Data["passed"].AsBool() == true;

    /// <summary>
    /// Fills the timing fields and applies the budget (a passing run over its budget fails), computes the artifact
    /// hash manifest for <paramref name="output"/> and the metrics block, writes result.json, and returns the process
    /// exit code: 0 when the report passed, <see cref="ExitBreak"/> when the run paused at a breakpoint, 1 otherwise.
    /// </summary>
    public int Finalize(string output)
    {
        FinalizeTiming(DateTimeOffset.UtcNow);
        CollectAttentions(output);
        if (EvidenceBudget.Artifacts(output, this) is { } hashes)
        {
            var manifest = new JsonObject();
            foreach (var (path, hash) in hashes)
            {
                manifest[path] = hash;
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
        if (Passed)
        {
            return 0;
        }

        return Has("break") ? ExitBreak : 1;
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

        if (Data[SoftFailuresKey] is not JsonArray failures)
        {
            failures = [];
            Data[SoftFailuresKey] = failures;
        }

        failures.Add(new JsonObject { ["check"] = check, ["error"] = error.Message });
        return false;
    }

    /// <summary>The failures <see cref="Expect"/> recorded, joined; null when none.</summary>
    public Exception? SoftFailure()
    {
        var errors = new List<Exception>();
        foreach (var failure in Data[SoftFailuresKey].AsSlice())
        {
            errors.Add(new AcceptanceException($"{failure.Member("check").AsString()}: {failure.Member("error").AsString()}"));
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
    /// Finalize (the series' drift flags) calls it again. A failure to write is not reported, as in Go.
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
            ordered["diagnosis"] = diagnosis?.DeepClone();
        }

        foreach (var key in Data.Select(pair => pair.Key).Where(key => key != "diagnosis").Order(StringComparer.Ordinal))
        {
            ordered[key] = Data[key]?.DeepClone();
        }

        return ordered.ToJsonString(WriteOptions);
    }
}
