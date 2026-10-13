using System.Text;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The sum, over every process of the run, of the instruments the series block reads (Go <c>metrics_otel.go</c>).
/// The run's native-call and pause costs come from the service's exported OTEL metrics
/// (docs/developers/contracts/otel-metrics.md), not from the log ring: every metrics.jsonl ring beside a service's
/// logs.jsonl holds cumulative instruments, so the last export of each process is its total and the run's value is
/// the sum over processes.
/// </summary>
internal sealed class OtelTotals
{
    private double _calls;
    private double _errs;
    private double _queueSum;
    private double _execSum;
    private double _queueCount;
    private double _execCount;
    private double _controllerPauseMs;

    /// <summary>The run's metric rings: the first launch's under output and each restart's under its service-N directory.</summary>
    internal static IReadOnlyList<string> MetricsRings(string output)
    {
        var paths = new List<string> { OtlpFiles.SiblingRing(ServiceLogs.ServiceLogsPath(output), OtlpSignal.Metrics) };
        if (Directory.Exists(output))
        {
            var matches = Directory.EnumerateDirectories(output, "service-*")
                .Select(dir => Path.Combine(dir, "otlp", OtlpFiles.Metrics))
                .Where(File.Exists)
                .Order(StringComparer.Ordinal);
            paths.AddRange(matches);
        }

        return paths;
    }

    /// <summary>Folds the run's metric rings. A missing or unreadable ring contributes nothing.</summary>
    internal static OtelTotals Read(string output)
    {
        var latest = new Dictionary<(string Instance, string Name, string Attrs), OtlpPoint>();
        foreach (var ring in MetricsRings(output))
        {
            try
            {
                OtlpReader.ReadMetrics(ring, metric =>
                {
                    if (!metric.Name.StartsWith("rimgovernor.", StringComparison.Ordinal))
                    {
                        return;
                    }

                    foreach (var point in metric.Points)
                    {
                        var key = (metric.Resource.Instance, metric.Name, AttrKey(point.Attrs));
                        if (!latest.TryGetValue(key, out var old) || point.TimeUnixNano >= old.TimeUnixNano)
                        {
                            latest[key] = point;
                        }
                    }
                });
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var totals = new OtelTotals();
        foreach (var (key, point) in latest)
        {
            switch (key.Name)
            {
                case TelemetryMetrics.NativeCallsMetric:
                    totals._calls += point.Value;
                    if (point.Attrs.Str("outcome") == "error")
                    {
                        totals._errs += point.Value;
                    }

                    break;
                case TelemetryMetrics.NativePhaseMetric:
                    switch (point.Attrs.Str("phase"))
                    {
                        case "native_queue":
                            totals._queueSum += point.Sum;
                            totals._queueCount += point.Count;
                            break;
                        case "native_execute":
                            totals._execSum += point.Sum;
                            totals._execCount += point.Count;
                            break;
                        default:
                            break;
                    }

                    break;
                case TelemetryMetrics.ControllerPauseMetric:
                    totals._controllerPauseMs += point.Sum;
                    break;
                default:
                    break;
            }
        }

        return totals;
    }

    /// <summary>Writes the OTEL-derived metrics into <paramref name="m"/>. <paramref name="wallMs"/> is the run's wall time.</summary>
    internal void Fill(Metrics m, double wallMs)
    {
        m.Values["native_calls"] = _calls;
        m.Values["native_errors"] = _errs;
        if (_queueCount > 0)
        {
            m.Values["native_queue_ms_mean"] = _queueSum / _queueCount;
        }

        if (_execCount > 0)
        {
            m.Values["native_exec_ms_mean"] = _execSum / _execCount;
        }

        if (wallMs > 0)
        {
            m.Values["paused_fraction"] = _controllerPauseMs / wallMs;
        }
    }

    private static string AttrKey(OtlpAttrDictionary attrs)
    {
        var builder = new StringBuilder();
        foreach (var key in attrs.Keys.Order(StringComparer.Ordinal))
        {
            builder.Append(key).Append('=');
            if (attrs[key] is string text)
            {
                builder.Append(text);
            }

            builder.Append(';');
        }

        return builder.ToString();
    }
}
