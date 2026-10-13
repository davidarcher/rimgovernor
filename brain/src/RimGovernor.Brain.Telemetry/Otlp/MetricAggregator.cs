using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Telemetry.Otlp;

internal enum MetricKind
{
    Gauge,
    Sum,
    Histogram,
}

/// <summary>One data point of a metric at one export; cumulative since the writer started.</summary>
internal sealed record PointSnapshot(
    IReadOnlyList<KeyValuePair<string, object?>> Attributes,
    DateTimeOffset? Start,
    DateTimeOffset Time,
    long IntValue,
    double Value,
    ulong Count,
    double Sum,
    double Min,
    double Max,
    ulong[] Buckets);

/// <summary>One metric with all its data points at one export.</summary>
internal sealed record MetricSnapshot(
    string Scope,
    string ScopeVersion,
    string Name,
    string Description,
    string Unit,
    MetricKind Kind,
    bool Monotonic,
    bool IsInt,
    double[] Bounds,
    IReadOnlyList<PointSnapshot> Points);

/// <summary>
/// Aggregates the process's <see cref="Meter"/> instruments (the ones under <see cref="Spans.ScopePrefix"/>) into
/// cumulative sums, last-value gauges and explicit-bucket histograms, the three kinds the file sink carries.
/// A measurement costs a tag sort, one string key and a short lock; an unlistened instrument costs nothing.
/// </summary>
internal sealed class MetricAggregator : IDisposable
{
    // The SDK's default explicit bounds, for a histogram that gives none.
    private static readonly double[] s_defaultBounds = [0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000];

    private readonly TimeProvider _time;
    private readonly DateTimeOffset _start;
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<Instrument, InstrumentState> _instruments = new();
    private readonly List<InstrumentState> _order = [];

    internal MetricAggregator(TimeProvider time)
    {
        _time = time;
        _start = time.GetUtcNow();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (!instrument.Meter.Name.StartsWith(Spans.ScopePrefix, StringComparison.Ordinal))
            {
                return;
            }
            var state = new InstrumentState(instrument);
            if (_instruments.TryAdd(instrument, state))
            {
                lock (_order)
                {
                    _order.Add(state);
                }
                listener.EnableMeasurementEvents(instrument, state);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, state) => Record(state, value, tags));
        _listener.SetMeasurementEventCallback<int>((_, value, tags, state) => Record(state, value, tags));
        _listener.SetMeasurementEventCallback<double>((_, value, tags, state) => Record(state, value, tags));
        _listener.SetMeasurementEventCallback<float>((_, value, tags, state) => Record(state, value, tags));
        _listener.Start();
    }

    private static void Record(object? state, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        ((InstrumentState)state!).Record(value, tags);

    /// <summary>Pulls the observable instruments, then snapshots every instrument that has recorded a point.</summary>
    internal List<MetricSnapshot> Collect()
    {
        _listener.RecordObservableInstruments();
        var now = _time.GetUtcNow();
        InstrumentState[] states;
        lock (_order)
        {
            states = [.. _order];
        }
        var result = new List<MetricSnapshot>(states.Length);
        foreach (var state in states)
        {
            if (state.Snapshot(_start, now) is { } snapshot)
            {
                result.Add(snapshot);
            }
        }
        return result;
    }

    public void Dispose() => _listener.Dispose();

    private sealed class InstrumentState
    {
        private readonly Instrument _instrument;
        private readonly MetricKind _kind;
        private readonly bool _monotonic;
        private readonly bool _observable;
        private readonly bool _isInt;
        private readonly double[] _bounds;
        private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.Ordinal);

        internal InstrumentState(Instrument instrument)
        {
            _instrument = instrument;
            var generic = instrument.GetType().GetGenericTypeDefinition();
            _observable = instrument.IsObservable;
            var argument = instrument.GetType().GetGenericArguments()[0];
            _isInt = argument == typeof(long) || argument == typeof(int) || argument == typeof(short) || argument == typeof(byte);
            (_kind, _monotonic) = generic switch
            {
                var g when g == typeof(Counter<>) || g == typeof(ObservableCounter<>) => (MetricKind.Sum, true),
                var g when g == typeof(UpDownCounter<>) || g == typeof(ObservableUpDownCounter<>) => (MetricKind.Sum, false),
                var g when g == typeof(Histogram<>) => (MetricKind.Histogram, false),
                _ => (MetricKind.Gauge, false),
            };
            _bounds = _kind == MetricKind.Histogram ? BoundsOf(instrument) : [];
        }

        private static double[] BoundsOf(Instrument instrument)
        {
            IReadOnlyList<double>? advised = instrument switch
            {
                Histogram<double> h => h.Advice?.HistogramBucketBoundaries,
                Histogram<float> h => h.Advice?.HistogramBucketBoundaries?.Select(b => (double)b).ToArray(),
                Histogram<long> h => h.Advice?.HistogramBucketBoundaries?.Select(b => (double)b).ToArray(),
                Histogram<int> h => h.Advice?.HistogramBucketBoundaries?.Select(b => (double)b).ToArray(),
                _ => null,
            };
            return advised is { Count: > 0 } ? [.. advised] : s_defaultBounds;
        }

        internal void Record(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var sorted = tags.ToArray();
            Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            var series = _series.GetOrAdd(KeyOf(sorted), _ => new Series(sorted, _bounds.Length + 1));
            lock (series)
            {
                switch (_kind)
                {
                    case MetricKind.Sum when _observable:
                    case MetricKind.Gauge:
                        series.Last = value;
                        series.LastInt = (long)value;
                        break;
                    case MetricKind.Sum:
                        if (_isInt)
                        {
                            series.LastInt += (long)value;
                        }
                        series.Last += value;
                        break;
                    default:
                        series.Count++;
                        series.Total += value;
                        series.Min = series.Count == 1 ? value : Math.Min(series.Min, value);
                        series.Max = series.Count == 1 ? value : Math.Max(series.Max, value);
                        series.Buckets[BucketOf(value)]++;
                        break;
                }
            }
        }

        private int BucketOf(double value)
        {
            var i = 0;
            while (i < _bounds.Length && value > _bounds[i])
            {
                i++;
            }
            return i;
        }

        internal MetricSnapshot? Snapshot(DateTimeOffset start, DateTimeOffset now)
        {
            var points = new List<PointSnapshot>(_series.Count);
            foreach (var key in _series.Keys.Order(StringComparer.Ordinal))
            {
                var s = _series[key];
                lock (s)
                {
                    points.Add(new PointSnapshot(
                        s.Tags,
                        _kind == MetricKind.Gauge ? null : start,
                        now,
                        s.LastInt,
                        s.Last,
                        s.Count,
                        s.Total,
                        s.Min,
                        s.Max,
                        [.. s.Buckets]));
                }
            }
            if (points.Count == 0)
            {
                return null;
            }
            return new MetricSnapshot(
                _instrument.Meter.Name,
                _instrument.Meter.Version ?? "",
                _instrument.Name,
                _instrument.Description ?? "",
                _instrument.Unit ?? "",
                _kind,
                _monotonic,
                _isInt,
                _bounds,
                points);
        }

        private static string KeyOf(KeyValuePair<string, object?>[] sorted)
        {
            if (sorted.Length == 0)
            {
                return "";
            }
            var sb = new StringBuilder();
            foreach (var (key, value) in sorted)
            {
                sb.Append(key).Append('=').Append(value?.GetType().Name).Append(':')
                    .Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\u001e');
            }
            return sb.ToString();
        }
    }

    private sealed class Series(KeyValuePair<string, object?>[] tags, int buckets)
    {
        internal KeyValuePair<string, object?>[] Tags { get; } = tags;

        internal ulong[] Buckets { get; } = new ulong[buckets];

        internal double Last;
        internal long LastInt;
        internal ulong Count;
        internal double Total;
        internal double Min;
        internal double Max;
    }
}
