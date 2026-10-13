namespace RimGovernor.Brain.Telemetry;

/// <summary>Severity of a telemetry row. There is no Debug level: Info is the floor (Go's slog levels Info, Warn, Error).</summary>
public enum TelemetryLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// One thing that happened at a planner, goal/method selection, admission or action-dispatch site: what was
/// decided (Verdict), why (Reason), about what (Target) and how long it took (Dur), plus the kind-specific facts
/// in Attrs. It is the only way to emit a decision row; a fresh kind needs an entry in the otel-signals contract
/// and a reader.
/// </summary>
public sealed record Decision
{
    /// <summary>Payload keys of a decision row. Every decision row carries all four scalar keys and an attrs object.</summary>
    public const string VerdictKey = "verdict";

    public const string ReasonKey = "reason";

    public const string TargetKey = "target";

    public const string DurMsKey = "dur_ms";

    public const string AttrsKey = "attrs";

    /// <summary>The row kind, a name from the otel-signals contract.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The subsystem that decided ("clock-scheduler", "worker").</summary>
    public string Component { get; init; } = "";

    /// <summary>Defaults to Info; a refusal that needs attention is Warn.</summary>
    public TelemetryLevel Level { get; init; } = TelemetryLevel.Info;

    /// <summary>The outcome word: admitted, refused, waiting, applied, failed, skipped, ok. Empty is written as "".</summary>
    public string Verdict { get; init; } = "";

    /// <summary>Why, a stable short word or phrase, not a sentence with data in it; the data goes in Attrs. Rows collapse on it.</summary>
    public string Reason { get; init; } = "";

    /// <summary>What the decision was about: a planner, goal, method, plan, pawn, zone or tool name.</summary>
    public string Target { get; init; } = "";

    /// <summary>How long the decision took; zero when it was not timed.</summary>
    public TimeSpan Dur { get; init; }

    /// <summary>The kind-specific fields. Values are scalars, lists or string-keyed maps; exceptions, durations (as milliseconds) and other objects (as text) are rendered by <see cref="Payload"/>.</summary>
    public IReadOnlyDictionary<string, object?> Attrs { get; init; } = new Dictionary<string, object?>();

    /// <summary>The row payload: the four fixed keys and attrs (an empty object when there are none).</summary>
    public IReadOnlyDictionary<string, object?> Payload()
    {
        var attrs = new Dictionary<string, object?>(Attrs.Count, StringComparer.Ordinal);
        foreach (var (k, v) in Attrs)
        {
            attrs[k] = AttrValue(v);
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VerdictKey] = Verdict,
            [ReasonKey] = Reason,
            [TargetKey] = Target,
            [DurMsKey] = Dur.TotalMilliseconds,
            [AttrsKey] = attrs,
        };
    }

    /// <summary>The log record's body for a decision row, "&lt;kind&gt; &lt;verdict&gt; &lt;reason&gt; &lt;target&gt;"; the row itself carries no free text.</summary>
    public string Summary()
    {
        var s = Kind;
        foreach (var part in new[] { Verdict, Reason, Target })
        {
            if (part.Length != 0)
            {
                s += " " + part;
            }
        }
        return s;
    }

    // Renders an attrs value: durations as milliseconds, exceptions as text; everything else is left to the encoder.
    private static object? AttrValue(object? v) => v switch
    {
        null => null,
        Exception e => e.Message,
        TimeSpan t => t.TotalMilliseconds,
        _ => v,
    };
}
