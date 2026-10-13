namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>The three signals; each has its own file ring so a busy one cannot rotate another's records out.</summary>
public enum OtlpSignal
{
    Traces,
    Logs,
    Metrics,
}

/// <summary>The file names inside the telemetry directory (docs/developers/contracts/otel-core.md, "File layout").</summary>
public static class OtlpFiles
{
    public const string Traces = "traces.jsonl";
    public const string Logs = "logs.jsonl";
    public const string Metrics = "metrics.jsonl";

    /// <summary>The ring file of <paramref name="signal"/> inside <paramref name="dir"/>.</summary>
    public static string RingPath(string dir, OtlpSignal signal) => Path.Combine(dir, signal switch
    {
        OtlpSignal.Traces => Traces,
        OtlpSignal.Logs => Logs,
        OtlpSignal.Metrics => Metrics,
        _ => throw new ArgumentOutOfRangeException(nameof(signal)),
    });

    /// <summary>The ring of another signal beside <paramref name="ring"/>, one of the three files.</summary>
    public static string SiblingRing(string ring, OtlpSignal signal) =>
        RingPath(Path.GetDirectoryName(ring) ?? "", signal);
}

/// <summary>Bounds one signal's file ring: disk use is at most <see cref="SegmentBytes"/> x <see cref="Segments"/>.</summary>
public readonly record struct OtlpLimits(long SegmentBytes, int Segments);

/// <summary>Configures <see cref="OtlpFileWriter.Start"/>. Every optional member defaults as the Go service did.</summary>
public sealed record OtlpFileConfig
{
    public const int DefaultQueueSize = 16384;
    public const int DefaultBatchSize = 512;
    public const int DefaultAttributeBytes = 64 << 10;

    /// <summary>Receives the OTLP/JSON files; required.</summary>
    public required string Dir { get; init; }

    /// <summary>service.name, for example "rimgovernor-mod".</summary>
    public string Service { get; init; } = "rimgovernor";

    /// <summary>service.version; empty for none.</summary>
    public string Version { get; init; } = "";

    /// <summary>service.instance.id, one per process launch; readers tell the runs sharing a ring apart by it. Empty for none.</summary>
    public string RunId { get; init; } = "";

    public OtlpLimits TraceLimits { get; init; } = new(16L << 20, 8);

    public OtlpLimits LogLimits { get; init; } = new(32L << 20, 16);

    public OtlpLimits MetricLimits { get; init; } = new(8L << 20, 4);

    /// <summary>Items a signal's queue holds before it drops and counts.</summary>
    public int QueueSize { get; init; } = DefaultQueueSize;

    /// <summary>Items one file write carries at most.</summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>The longest a queued span or log record waits for its write.</summary>
    public TimeSpan FileFlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the metrics are collected into <c>metrics.jsonl</c>.</summary>
    public TimeSpan MetricInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Caps one attribute string value.</summary>
    public int MaxAttributeBytes { get; init; } = DefaultAttributeBytes;

    /// <summary>Receives the rate-limited drop warning (at most one per pipeline per 10 s); the counter moves regardless.</summary>
    public Action<string>? Warn { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>The file ring could not be opened or written.</summary>
public sealed class OtlpException : IOException
{
    public OtlpException(string message)
        : base("otel: " + message)
    {
    }

    public OtlpException(string message, Exception inner)
        : base("otel: " + message, inner)
    {
    }
}
