using System.Globalization;
using System.Text;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Telemetry.Otlp;

public sealed record ForecastEvidenceRead(IReadOnlyList<ForecastPrediction> Predictions,
    IReadOnlyList<ForecastObservation> Observations, IReadOnlyList<string> Gaps)
{ public IReadOnlyList<ForecastHandoffRead> Handoffs { get; init; } = []; }

/// <summary>Typed residual evidence decoder. Read gaps never turn into zero-valued samples.</summary>
public static class ForecastEvidenceReader
{
    public const string PredictionKind = "forecast_prediction";
    public const string ObservationKind = "forecast_observation";

    public static ForecastEvidenceRead Read(string path)
    {
        var rows = new List<TimelineRecord>();
        var gaps = new List<string>();
        var segments = OtlpRing.Segments(path);
        if (segments.Count == 0) gaps.Add("missing_logs");
        // Read complete lines directly: the general timeline reader intentionally skips malformed/torn lines.
        foreach (var segment in segments)
        {
            try
            {
                using var stream = new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var terminated = stream.Length == 0;
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    terminated = stream.ReadByte() == '\n';
                    stream.Seek(0, SeekOrigin.Begin);
                }
                using var reader = new StreamReader(stream);
                var number = 0;
                while (reader.ReadLine() is { } line)
                {
                    number++;
                    if (reader.EndOfStream && !terminated)
                    {
                        gaps.Add($"unterminated_segment:{Path.GetFileName(segment)}");
                        break;
                    }
                    var log = OtlpReader.DecodeLog(Encoding.UTF8.GetBytes(line));
                    if (log is null) gaps.Add($"invalid_record:{Path.GetFileName(segment)}:{number}");
                    else if (OtlpTimeline.FromLog(log) is { } row && (row.Kind == PredictionKind || row.Kind == ObservationKind || row.Kind == TelemetryKinds.CoupledSelection.Name)) rows.Add(row);
                }

            }
            catch (FileNotFoundException) { gaps.Add($"rotated_during_read:{Path.GetFileName(segment)}"); }
        }
        var decoded = Decode(rows);
        return decoded with { Gaps = gaps.Concat(decoded.Gaps).ToArray() };
    }

    public static ForecastEvidenceRead Decode(IEnumerable<TimelineRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var predictions = new List<ForecastPrediction>();
        var observations = new List<ForecastObservation>();
        var handoffs = new List<ForecastHandoffRead>();
        var gaps = new List<string>();
        foreach (var row in rows)
        {
            if (ForecastHandoffReader.Decode(row) is { } handoff) handoffs.Add(handoff);
            if (row.Kind != TelemetryKinds.ForecastPrediction.Name && row.Kind != TelemetryKinds.ForecastObservation.Name) continue;
            if (row.Payload.GetValueOrDefault("truncated") is true) { gaps.Add("truncated_record"); continue; }
            try
            {
                var p = row.Payload.TryGetValue(Decision.AttrsKey, out var attrs) && attrs is IReadOnlyDictionary<string, object?> fields ? fields : throw new FormatException(Decision.AttrsKey);
                string Text(string key) => p.TryGetValue(key, out var value) && value is string text ? text : throw new FormatException(key);
                string Required(string key) => Text(key) is { Length: > 0 } text ? text : throw new FormatException(key);
                long Tick(string key) => p.TryGetValue(key, out var value)
                    && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out var number)
                    && number >= 0 ? number : throw new FormatException(key);
                T EnumValue<T>(string key) where T : struct, Enum => Enum.TryParse<T>(Text(key), out var value)
                    && Enum.IsDefined(value) ? value : throw new FormatException(key);
                double? Number(string key) => !p.TryGetValue(key, out var value) || value is null ? null
                    : double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out var number)
                      && double.IsFinite(number) ? number : throw new FormatException(key);
                double Bound(string key) => Number(key) ?? throw new FormatException(key);
                var key = new ForecastEvidenceKey(Required("run"), Required("load"), Required("map"), Required("review"),
                    Required("sample"), EnumValue<ForecastMetric>("metric"), Required("subject"), Required("scope"),
                    Required("unit"), Text("method"), Text("task"), Tick("revision"));
                var unknown = Text("unknown_reason");
                EvidenceValue value = Number("value") is { } number ? new EvidenceValue.Valued(number)
                    : unknown.Length > 0 ? new EvidenceValue.Unavailable(unknown) : throw new FormatException("unknown_reason");
                if (row.Kind == TelemetryKinds.ForecastPrediction.Name)
                    predictions.Add(new(key, Tick("origin_tick"), Tick("target_tick"), value, EnumValue<ForecastAdmission>("admission")));
                else
                {
                    ForecastCompletion completion = Text("completion") switch
                    {
                        nameof(ForecastCompletion.NotApplicable) => new ForecastCompletion.NotApplicable(),
                        nameof(ForecastCompletion.Exact) => new ForecastCompletion.Exact(),
                        nameof(ForecastCompletion.Interval) => new ForecastCompletion.Interval(Bound("lower_tick"), Bound("upper_tick")),
                        nameof(ForecastCompletion.CompletedBy) => new ForecastCompletion.CompletedBy(Bound("upper_tick")),
                        nameof(ForecastCompletion.Unfinished) => new ForecastCompletion.Unfinished(),
                        nameof(ForecastCompletion.Unobserved) => new ForecastCompletion.Unobserved(),
                        _ => throw new FormatException("completion"),
                    };
                    observations.Add(new(key, Tick("observation_tick"), value, EnumValue<ForecastExecution>("execution"),
                        completion, Required("provenance")));
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            { gaps.Add($"invalid_evidence:{ex.Message}"); }
        }
        var unique = new List<ForecastPrediction>();
        foreach (var group in predictions.GroupBy(p => Correlation(p.Key)))
        {
            var distinct = group.Distinct().ToArray();
            if (distinct.Length != 1) gaps.Add($"conflicting_predictions:{group.Key}");
            else unique.Add(distinct[0]);
            if (group.Count() > distinct.Length) gaps.Add($"duplicate_predictions:{group.Key}");
        }
        var correlations = unique.Select(p => Correlation(p.Key)).ToHashSet();
        foreach (var observation in observations.Where(o => !correlations.Contains(Correlation(o.Key))))
            gaps.Add($"missing_prediction:{Correlation(observation.Key)}");
        var duplicates = observations.Count - observations.Distinct().Count();
        if (duplicates > 0) gaps.Add($"duplicate_observations:{duplicates}");
        if (unique.Count == 0) gaps.Add("no_predictions");
        return new(unique, observations, gaps) { Handoffs = handoffs };
    }

    private static (string Run, string Load, string Map, string Review, string Sample) Correlation(ForecastEvidenceKey k) =>
        (k.Run, k.Load, k.Map, k.Review, k.Sample);

    public static Dictionary<string, object?> Fields(ForecastPrediction prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        var fields = Fields(prediction.Key);
        fields["origin_tick"] = prediction.OriginTick;
        fields["target_tick"] = prediction.TargetTick;
        Value(fields, prediction.Value);
        fields["admission"] = prediction.Admission.ToString();
        return fields;
    }

    public static Dictionary<string, object?> Fields(ForecastObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var fields = Fields(observation.Key);
        fields["observation_tick"] = observation.Tick;
        Value(fields, observation.Value);
        fields["execution"] = observation.Execution.ToString();
        fields["completion"] = CompletionName(observation.Completion);
        fields["lower_tick"] = observation.Completion is ForecastCompletion.Interval(var lo, _) ? lo : null;
        fields["upper_tick"] = observation.Completion is ForecastCompletion.Interval(_, var hi) ? hi
            : observation.Completion is ForecastCompletion.CompletedBy(var by) ? by : null;
        fields["provenance"] = observation.Provenance;
        return fields;
    }

    /// <summary>The <c>completion</c> attribute: the completion case's name.</summary>
    public static string CompletionName(ForecastCompletion completion) => completion.GetType().Name;

    private static void Value(Dictionary<string, object?> fields, EvidenceValue value)
    {
        fields["value"] = value is EvidenceValue.Valued(var number) ? number : null;
        fields["unknown_reason"] = value is EvidenceValue.Unavailable(var reason) ? reason : "";
    }

    private static Dictionary<string, object?> Fields(ForecastEvidenceKey k) => new(StringComparer.Ordinal)
    {
        ["run"] = k.Run, ["load"] = k.Load, ["map"] = k.Map, ["review"] = k.Review, ["sample"] = k.Sample,
        ["metric"] = k.Metric.ToString(), ["subject"] = k.Subject, ["scope"] = k.Scope, ["unit"] = k.Unit,
        ["method"] = k.Method, ["task"] = k.Task, ["revision"] = k.Revision.ToString(CultureInfo.InvariantCulture),
    };
}


