using System.Threading.Channels;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>Drop reasons (the bounded <c>reason</c> dimension of the dropped metric).</summary>
public static class OtlpDropReason
{
    public const string QueueFull = "queue_full";
    public const string ExportFailed = "export_failed";
}

/// <summary>One pipeline's cumulative loss, for tests and status surfaces.</summary>
public readonly record struct DropCount(string Signal, string Sink, string Reason, long Count);

/// <summary>Counts what one pipeline (a signal going to a sink) lost and warns at most once per <c>warnEvery</c>.</summary>
internal sealed class DropStats(string signal, string sink, TimeSpan warnEvery, Action<string>? warn, TimeProvider time)
{
    private long _queueFull;
    private long _exportFailed;
    private long _lastWarn;
    private long _unwarned;

    internal string Signal { get; } = signal;

    internal string Sink { get; } = sink;

    internal void Add(string reason, long n)
    {
        if (n == 0)
        {
            return;
        }
        if (reason == OtlpDropReason.QueueFull)
        {
            Interlocked.Add(ref _queueFull, n);
        }
        else
        {
            Interlocked.Add(ref _exportFailed, n);
        }
        Interlocked.Add(ref _unwarned, n);
        var now = time.GetTimestamp();
        var last = Interlocked.Read(ref _lastWarn);
        if ((last != 0 && time.GetElapsedTime(last, now) < warnEvery)
            || Interlocked.CompareExchange(ref _lastWarn, now, last) != last)
        {
            return;
        }
        var lost = Interlocked.Exchange(ref _unwarned, 0);
        if (lost > 0 && warn is not null)
        {
            warn($"telemetry dropped items signal={Signal} sink={Sink} reason={reason} dropped_since_last_warning={lost}");
        }
    }

    internal DropCount[] Counts() =>
    [
        new(Signal, Sink, OtlpDropReason.QueueFull, Interlocked.Read(ref _queueFull)),
        new(Signal, Sink, OtlpDropReason.ExportFailed, Interlocked.Read(ref _exportFailed)),
    ];
}

/// <summary>
/// A bounded asynchronous hand-off from a hot path to one exporter. <see cref="Offer"/> never blocks: a full
/// queue drops the item and counts it. One task drains the queue in batches and calls the export delegate; a
/// failing exporter holds only that task while the queue fills and drops, so no caller ever waits on a sink.
/// An item for which <c>urgent</c> answers true makes its batch export at once and durable.
/// </summary>
internal sealed class BatchQueue<T>
{
    private readonly record struct Entry(T? Item, TaskCompletionSource? Flush);

    private readonly DropStats _stats;
    private readonly int _size;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly Func<T, bool>? _urgent;
    private readonly Action<List<T>, bool> _export;
    private readonly TimeProvider _time;
    private readonly Channel<Entry> _channel = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _run;
    private int _queued;
    private volatile bool _stopped;

    internal BatchQueue(DropStats stats, int size, int batchSize, TimeSpan flushInterval, Func<T, bool>? urgent, Action<List<T>, bool> export, TimeProvider time)
    {
        _stats = stats;
        _size = size;
        _batchSize = batchSize;
        _flushInterval = flushInterval;
        _urgent = urgent;
        _export = export;
        _time = time;
        _run = Task.Run(RunAsync);
    }

    /// <summary>Enqueues without blocking and reports whether the item was kept. The bound is on queued items, not on flush requests.</summary>
    internal bool Offer(T item)
    {
        if (!_stopped && Interlocked.Increment(ref _queued) <= _size)
        {
            if (_channel.Writer.TryWrite(new Entry(item, null)))
            {
                return true;
            }
        }
        if (!_stopped)
        {
            Interlocked.Decrement(ref _queued);
        }
        _stats.Add(OtlpDropReason.QueueFull, 1);
        return false;
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<T>(_batchSize);
        var durable = false;
        var flushed = new List<TaskCompletionSource>();

        void Flush()
        {
            if (batch.Count > 0)
            {
                try
                {
                    _export(batch, durable);
                }
                catch (IOException)
                {
                    _stats.Add(OtlpDropReason.ExportFailed, batch.Count);
                }
                batch.Clear();
                durable = false;
            }
            foreach (var ack in flushed)
            {
                ack.TrySetResult();
            }
            flushed.Clear();
        }

        while (true)
        {
            if (batch.Count == 0)
            {
                if (!await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    break;
                }
            }
            else
            {
                using var cts = new CancellationTokenSource(_flushInterval, _time);
                try
                {
                    if (!await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    Flush();
                    continue;
                }
            }
            while (reader.TryRead(out var entry))
            {
                if (entry.Flush is not null)
                {
                    flushed.Add(entry.Flush);
                    Flush();
                    continue;
                }
                Interlocked.Decrement(ref _queued);
                batch.Add(entry.Item!);
                if (_urgent is not null && _urgent(entry.Item!))
                {
                    durable = true;
                }
                if (durable || batch.Count >= _batchSize)
                {
                    Flush();
                }
            }
        }
        Flush();
    }

    /// <summary>Exports everything queued before the call.</summary>
    internal async Task ForceFlushAsync(CancellationToken ct)
    {
        if (_stopped)
        {
            return;
        }
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new Entry(default, ack)))
        {
            return;
        }
        await ack.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Drains and stops the task; items offered afterwards are dropped and counted.</summary>
    internal async Task ShutdownAsync(CancellationToken ct)
    {
        _stopped = true;
        _channel.Writer.TryComplete();
        await _run.WaitAsync(ct).ConfigureAwait(false);
    }
}
