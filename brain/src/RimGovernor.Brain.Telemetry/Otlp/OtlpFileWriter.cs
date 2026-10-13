using System.Buffers;
using System.Diagnostics;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>
/// The production <see cref="ITelemetrySink"/>: writes the process's telemetry as the local OTLP/JSON file rings
/// the Go service wrote (contract: docs/developers/contracts/otel-core.md). Log records come from
/// <see cref="TelemetryEmitter"/> rows, spans from the <see cref="Spans"/> activity sources and metrics from the
/// <see cref="TelemetryMetrics"/> meter, each under <see cref="Spans.ScopePrefix"/>. Nothing here is a state store and
/// nothing can block a caller: every signal has its own bounded queue and one writer task, and a full queue drops
/// and counts. There is no collector exporter; the files are the interface (a collector's file receiver reads them).
/// </summary>
public sealed class OtlpFileWriter : ITelemetrySink, IAsyncDisposable
{
    /// <summary>Counter of items lost, with bounded attributes signal (trace|log|metric), sink (file) and reason (queue_full|export_failed).</summary>
    public const string DroppedMetric = "rimgovernor.telemetry.dropped";

    private const string DroppedScope = "rimgovernor/otel";

    private readonly OtlpFileConfig _config;
    private readonly string _dir;
    private readonly RingFile _traceFile;
    private readonly RingFile _logFile;
    private readonly RingFile _metricFile;
    private readonly DropStats _traceStats;
    private readonly DropStats _logStats;
    private readonly DropStats _metricStats;
    private readonly BatchQueue<Activity> _traces;
    private readonly BatchQueue<TelemetryRecord> _logs;
    private readonly ActivityListener _activities;
    private readonly MetricAggregator _metrics;
    private readonly OtlpEncoder _metricEncoder;
    private readonly OtlpEncoder _spanEncoder;
    private readonly OtlpEncoder _logEncoder;
    private readonly object _metricGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _metricLoop;
    private readonly DateTimeOffset _started;
    private int _shutdown;

    private OtlpFileWriter(OtlpFileConfig config)
    {
        _config = config;
        _dir = config.Dir;
        _started = config.Time.GetUtcNow();
        var warn = TimeSpan.FromSeconds(10);
        _traceStats = new DropStats("trace", "file", warn, config.Warn, config.Time);
        _logStats = new DropStats("log", "file", warn, config.Warn, config.Time);
        _metricStats = new DropStats("metric", "file", warn, config.Warn, config.Time);

        _traceFile = new RingFile(OtlpFiles.RingPath(_dir, OtlpSignal.Traces), config.TraceLimits.SegmentBytes, config.TraceLimits.Segments);
        try
        {
            _logFile = new RingFile(OtlpFiles.RingPath(_dir, OtlpSignal.Logs), config.LogLimits.SegmentBytes, config.LogLimits.Segments);
            try
            {
                _metricFile = new RingFile(OtlpFiles.RingPath(_dir, OtlpSignal.Metrics), config.MetricLimits.SegmentBytes, config.MetricLimits.Segments);
            }
            catch
            {
                _logFile.Close();
                throw;
            }
        }
        catch
        {
            _traceFile.Close();
            throw;
        }

        _spanEncoder = new OtlpEncoder(config);
        var spanEncoder = _spanEncoder;
        var spanBuffer = new ArrayBufferWriter<byte>(64 << 10);
        _traces = new BatchQueue<Activity>(
            _traceStats,
            config.QueueSize,
            config.BatchSize,
            config.FileFlushInterval,
            IsDurable,
            (batch, durable) => WriteBatch(_traceFile, _traceStats, spanBuffer, batch, durable, (dst, a) => spanEncoder.EncodeSpan(dst, a), spanEncoder),
            config.Time);

        _logEncoder = new OtlpEncoder(config);
        var logEncoder = _logEncoder;
        var logBuffer = new ArrayBufferWriter<byte>(64 << 10);
        _logs = new BatchQueue<TelemetryRecord>(
            _logStats,
            config.QueueSize,
            config.BatchSize,
            config.FileFlushInterval,
            static r => r.Durable,
            (batch, durable) => WriteBatch(_logFile, _logStats, logBuffer, batch, durable, (dst, r) => logEncoder.EncodeLog(dst, r), logEncoder),
            config.Time);

        _metricEncoder = new OtlpEncoder(config);
        _metrics = new MetricAggregator(config.Time);

        _activities = new ActivityListener
        {
            ShouldListenTo = static source => source.Name.StartsWith(Spans.ScopePrefix, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _traces.Offer(activity),
        };
        ActivitySource.AddActivityListener(_activities);

        _metricLoop = Task.Run(() => MetricLoopAsync(_stop.Token));
    }

    /// <summary>
    /// Opens the three rings in <see cref="OtlpFileConfig.Dir"/> and starts recording. Throws
    /// <see cref="OtlpException"/> for an unusable directory; the caller then runs without telemetry.
    /// </summary>
    public static OtlpFileWriter Start(OtlpFileConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Dir))
        {
            throw new OtlpException("telemetry directory is empty");
        }
        if (config.QueueSize < 1 || config.BatchSize < 1 || config.MaxAttributeBytes < 1
            || config.FileFlushInterval <= TimeSpan.Zero || config.MetricInterval <= TimeSpan.Zero)
        {
            throw new OtlpException("queue, batch, attribute and interval bounds must be positive");
        }
        return new OtlpFileWriter(config);
    }

    /// <summary>The active file of <paramref name="signal"/>.</summary>
    public string FilePath(OtlpSignal signal) => OtlpFiles.RingPath(_dir, signal);

    /// <summary>Cumulative loss per signal, sink and reason.</summary>
    public IReadOnlyList<DropCount> Drops() => [.. _traceStats.Counts(), .. _logStats.Counts(), .. _metricStats.Counts()];

    /// <inheritdoc/>
    public bool Enabled(TelemetryLevel severity, string kind) => Volatile.Read(ref _shutdown) == 0;

    /// <inheritdoc/>
    public void Emit(TelemetryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _logs.Offer(record);
    }

    /// <summary>Writes everything queued so far, and a metric collection, or returns when <paramref name="ct"/> ends.</summary>
    public async Task ForceFlushAsync(CancellationToken ct)
    {
        await _traces.ForceFlushAsync(ct).ConfigureAwait(false);
        await _logs.ForceFlushAsync(ct).ConfigureAwait(false);
        ExportMetrics();
    }

    /// <summary>
    /// Flushes and stops every pipeline, runs a final metric collection and closes the files (fsync'd). Items
    /// offered after shutdown begins are dropped and counted. Safe to call more than once.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }
        _activities.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _metricLoop.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _traces.ShutdownAsync(ct).ConfigureAwait(false);
            await _logs.ShutdownAsync(ct).ConfigureAwait(false);
            ExportMetrics();
        }
        finally
        {
            _metrics.Dispose();
            _spanEncoder.Dispose();
            _logEncoder.Dispose();
            _metricEncoder.Dispose();
            _traceFile.Close();
            _logFile.Close();
            _metricFile.Close();
            _stop.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);

    private static bool IsDurable(Activity activity)
    {
        foreach (var (key, value) in activity.TagObjects)
        {
            if (key == RowKeys.LogDurable && value is true)
            {
                return true;
            }
        }
        return false;
    }

    // Encodes the batch into one buffer and appends it with a single write. An item that cannot be encoded costs
    // itself only.
    private static void WriteBatch<T>(RingFile file, DropStats stats, ArrayBufferWriter<byte> buffer, List<T> batch, bool durable, Action<IBufferWriter<byte>, T> encode, OtlpEncoder encoder)
    {
        buffer.Clear();
        foreach (var item in batch)
        {
            var mark = buffer.WrittenCount;
            try
            {
                encode(buffer, item);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
            {
                encoder.Reset();
                // Roll the partial line back: ArrayBufferWriter cannot truncate, so rebuild without it.
                var kept = buffer.WrittenSpan[..mark].ToArray();
                buffer.Clear();
                buffer.Write(kept);
                stats.Add(OtlpDropReason.ExportFailed, 1);
            }
        }
        if (buffer.WrittenCount > 0)
        {
            file.Write(buffer.WrittenSpan, durable);
        }
        if (buffer.Capacity > 4 << 20)
        {
            buffer.Clear();
        }
    }

    private async Task MetricLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_config.MetricInterval, _config.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                ExportMetrics();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: the final collection is the caller's.
        }
    }

    // Pull-based: instruments aggregate in memory and are written here, so they never wait on the file.
    private void ExportMetrics()
    {
        lock (_metricGate)
        {
            var buffer = new ArrayBufferWriter<byte>(16 << 10);
            var snapshots = _metrics.Collect();
            snapshots.Add(DroppedSnapshot());
            foreach (var snapshot in snapshots)
            {
                var mark = buffer.WrittenCount;
                try
                {
                    _metricEncoder.EncodeMetric(buffer, snapshot);
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
                {
                    _metricEncoder.Reset();
                    var kept = buffer.WrittenSpan[..mark].ToArray();
                    buffer.Clear();
                    buffer.Write(kept);
                    _metricStats.Add(OtlpDropReason.ExportFailed, 1);
                }
            }
            if (buffer.WrittenCount == 0)
            {
                return;
            }
            try
            {
                _metricFile.Write(buffer.WrittenSpan, durable: false);
            }
            catch (IOException)
            {
                _metricStats.Add(OtlpDropReason.ExportFailed, 1);
            }
        }
    }

    private MetricSnapshot DroppedSnapshot()
    {
        var now = _config.Time.GetUtcNow();
        var points = new List<PointSnapshot>();
        foreach (var d in Drops())
        {
            points.Add(new PointSnapshot(
                [new("reason", d.Reason), new("signal", d.Signal), new("sink", d.Sink)],
                _started,
                now,
                d.Count,
                d.Count,
                0,
                0,
                0,
                0,
                []));
        }
        return new MetricSnapshot(
            DroppedScope,
            "",
            DroppedMetric,
            "Telemetry items lost before export, by signal, sink and reason.",
            "{item}",
            MetricKind.Sum,
            Monotonic: true,
            IsInt: true,
            [],
            points);
    }
}
