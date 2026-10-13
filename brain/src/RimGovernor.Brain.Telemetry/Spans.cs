using System.Collections.Concurrent;
using System.Diagnostics;

namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// Spans are the control loop's correlation: a scheduler step is a trace root; round review, each planner, plan
/// admission, the worker's step, a hands dispatch and every native bridge call are spans under it, carried on
/// <see cref="Activity.Current"/>. They are exported by the OTEL providers; with no listener attached they are
/// null and cost nothing.
/// </summary>
public static class Spans
{
    /// <summary>Names every instrumentation scope this process creates.</summary>
    public const string ScopePrefix = "github.com/davidarcher/RimGovernor/";

    private static readonly ConcurrentDictionary<string, ActivitySource> s_sources = new(StringComparer.Ordinal);

    /// <summary>The activity source of a component's instrumentation scope.</summary>
    public static ActivitySource Source(string component) =>
        s_sources.GetOrAdd(component, static c => new ActivitySource(ScopePrefix + c));

    /// <summary>
    /// Starts a span named <paramref name="name"/> under the current activity (a new trace root when there is
    /// none), or under <paramref name="parent"/> when one is given. Null when no listener samples the scope.
    /// </summary>
    public static Activity? StartSpan(string component, string name, ActivityContext parent = default) =>
        Source(component).StartActivity(name, ActivityKind.Internal, parent);

    /// <summary>Ends <paramref name="span"/>, marking it an error when <paramref name="error"/> is set.</summary>
    public static void EndSpan(Activity? span, Exception? error = null)
    {
        if (span is null)
        {
            return;
        }
        if (error is not null)
        {
            span.AddException(error);
            span.SetStatus(ActivityStatusCode.Error, error.Message);
        }
        span.Stop();
    }

    /// <summary>
    /// Marks <paramref name="span"/> an error for a refusal (a dispatch the game declined, an admission that was
    /// not granted), which is not an exception but is a failed outcome. <paramref name="reasons"/> are the stable
    /// refusal words.
    /// </summary>
    public static void Refuse(Activity? span, params string[] reasons)
    {
        if (span is null)
        {
            return;
        }
        span.SetStatus(ActivityStatusCode.Error, "refused");
        span.SetTag("rimgovernor.refused", reasons);
    }

    /// <summary>
    /// The context of the span named by hex trace and span ids as a remote parent (an id pair from another
    /// process, e.g. the mod's echoed traceparent), so a span started under it is a child of it even though its
    /// span lives elsewhere. Malformed ids give the default context, which starts a new root.
    /// </summary>
    public static ActivityContext RemoteHex(string traceId, string spanId)
    {
        if (!IsHex(traceId, 32) || !IsHex(spanId, 16))
        {
            return default;
        }
        var trace = ActivityTraceId.CreateFromString(traceId);
        var span = ActivitySpanId.CreateFromString(spanId);
        if (trace == default || span == default)
        {
            return default;
        }
        return new ActivityContext(trace, span, ActivityTraceFlags.Recorded, isRemote: true);
    }

    /// <summary>
    /// The W3C traceparent of the current activity, the form a bridge call sends the companion
    /// ("00-&lt;32 hex trace&gt;-&lt;16 hex span&gt;-&lt;flags&gt;"), or empty when there is none. The companion echoes it opaquely.
    /// </summary>
    public static string Traceparent()
    {
        var current = Activity.Current;
        if (current is null || current.TraceId == default || current.SpanId == default)
        {
            return "";
        }
        var flags = (byte)current.ActivityTraceFlags;
        return $"00-{current.TraceId.ToHexString()}-{current.SpanId.ToHexString()}-{flags:x2}";
    }

    // The hex digits of an id are lowercase; an all-zero id is invalid.
    private static bool IsHex(string s, int length)
    {
        if (s.Length != length)
        {
            return false;
        }
        foreach (var c in s)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Splits what a companion log line or timing block carries: a W3C traceparent
    /// ("00-&lt;trace&gt;-&lt;span&gt;-&lt;flags&gt;") or the older "&lt;trace&gt;/&lt;span&gt;". False for anything else.
    /// </summary>
    public static bool TryParseWireTrace(string wire, out string traceId, out string spanId)
    {
        var parts = wire.Split('-');
        if (parts.Length == 4 && parts[1].Length == 32 && parts[2].Length == 16)
        {
            (traceId, spanId) = (parts[1], parts[2]);
            return true;
        }
        var cut = wire.IndexOf('/', StringComparison.Ordinal);
        if (cut > 0 && cut < wire.Length - 1)
        {
            (traceId, spanId) = (wire[..cut], wire[(cut + 1)..]);
            return true;
        }
        (traceId, spanId) = ("", "");
        return false;
    }
}
