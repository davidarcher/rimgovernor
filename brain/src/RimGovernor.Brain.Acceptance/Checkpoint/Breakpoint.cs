using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Acceptance;

// A breakpoint stops a run at a named point of its run phase so the colony can be inspected instead of diagnosed
// from text: the case is cut, a bundle labelled BreakCheckpoint is taken into its ring, and the game is left
// loaded and paused on the kept process (visible with -headless=false). The ring's Next names the bundle, so
// `acceptance resume` (or the next plain run of the case) continues from it and `acceptance stop` discards it.

/// <summary>
/// Where a run stops: after the named stage (Session.Stage), at the first natural pause once the game tick reaches
/// <see cref="Tick"/>, or once the run-phase offset (the ring's t+... labels) reaches <see cref="Minute"/>. Exactly
/// one is set.
/// </summary>
public readonly record struct Breakpoint(string Stage, ulong Tick, TimeSpan Minute)
{
    /// <summary>The label of the bundle a run paused at a breakpoint leaves in its ring.</summary>
    public const string BreakCheckpoint = "break";

    /// <summary>True when no breakpoint is set.</summary>
    public bool IsZero => string.IsNullOrEmpty(Stage) && Tick == 0 && Minute == TimeSpan.Zero;

    /// <summary>Reads a -break spec: stage=&lt;name&gt;, tick=&lt;n&gt; or minute=&lt;m&gt; (a whole number of minutes or a duration such as 1m30s).</summary>
    public static Breakpoint Parse(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var trimmed = spec.Trim();
        var cut = trimmed.IndexOf('=', StringComparison.Ordinal);
        var kind = cut < 0 ? trimmed : trimmed[..cut];
        var value = cut < 0 ? "" : trimmed[(cut + 1)..];
        if (cut < 0 || value.Length == 0)
        {
            throw new AcceptanceException($"-break \"{spec}\": want stage=<name>, tick=<n> or minute=<m>");
        }

        switch (kind)
        {
            case "stage":
                return new Breakpoint(value, 0, TimeSpan.Zero);
            case "tick":
                if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0)
                {
                    throw new AcceptanceException($"-break \"{spec}\": the tick must be a positive integer");
                }

                return new Breakpoint("", n, TimeSpan.Zero);
            case "minute":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && double.IsFinite(minutes))
                {
                    if (minutes <= 0)
                    {
                        throw new AcceptanceException($"-break \"{spec}\": the minute must be positive");
                    }

                    return new Breakpoint("", 0, TimeSpan.FromTicks((long)(minutes * TimeSpan.TicksPerMinute)));
                }

                if (!GoDuration.TryParse(value, out var d) || d <= TimeSpan.Zero)
                {
                    throw new AcceptanceException($"-break \"{spec}\": the minute must be a positive number of minutes or a duration");
                }

                return new Breakpoint("", 0, d);
            default:
                throw new AcceptanceException($"-break \"{spec}\": unknown kind \"{kind}\" (stage, tick or minute)");
        }
    }

    /// <summary>The spec <see cref="Parse"/> reads.</summary>
    public override string ToString()
    {
        if (!string.IsNullOrEmpty(Stage))
        {
            return "stage=" + Stage;
        }

        if (Tick > 0)
        {
            return "tick=" + Tick.ToString(CultureInfo.InvariantCulture);
        }

        if (Minute > TimeSpan.Zero)
        {
            return Minute.Ticks % TimeSpan.TicksPerMinute == 0
                ? "minute=" + (Minute.Ticks / TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture)
                : "minute=" + GoDuration.Format(Minute);
        }

        return "";
    }
}

/// <summary>
/// What a ring remembers about the run that paused at a breakpoint (<see cref="Ring.Break"/>): the spec, why it
/// tripped, the bundle's tick and the run's output directory.
/// </summary>
public sealed class BreakRecord
{
    public string Spec { get; set; } = "";

    public string Reason { get; set; } = "";

    public ulong Tick { get; set; }

    public string Output { get; set; } = "";

    public string At { get; set; } = "";

    internal JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["spec"] = Spec,
            ["reason"] = Reason,
            ["tick"] = Tick,
        };
        if (Output.Length > 0)
        {
            o["output"] = Output;
        }

        o["at"] = At;
        return o;
    }

    internal static BreakRecord FromJson(JsonObject o) => new()
    {
        Spec = o["spec"].AsString(),
        Reason = o["reason"].AsString(),
        Tick = o["tick"].AsUInt64() ?? 0,
        Output = o["output"].AsString(),
        At = o["at"].AsString(),
    };
}

/// <summary>The cause a tripped breakpoint cancels the run with; <see cref="Broke"/> reads it back.</summary>
public sealed class BreakException : OperationCanceledException
{
    public BreakException(string reason)
        : base("breakpoint: " + reason)
    {
        Reason = reason;
    }

    public string Reason { get; }

    /// <summary>The breakpoint that cut a run, null when the exception is any other (or none).</summary>
    public static BreakException? Broke(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is BreakException found)
            {
                return found;
            }
        }

        return null;
    }
}

public sealed partial class CheckpointRing
{
    /// <summary>How often a tick breakpoint reads the game tick at a natural pause (<see cref="ReadTickAsync"/>).</summary>
    private static readonly long BreakTickPoll = ToTimestamp(TimeSpan.FromSeconds(2));

    /// <summary>Fires the ring's breakpoint once with reason: OnBreak (the runner's cancel) runs on the first call only.</summary>
    public void Trip(string reason)
    {
        Action<string>? on;
        lock (_gate)
        {
            if (_tripped.Length > 0)
            {
                return;
            }

            _tripped = reason;
            on = OnBreak;
        }

        on?.Invoke(reason);
    }

    /// <summary>Why the breakpoint fired, "" while it has not.</summary>
    public string Tripped
    {
        get
        {
            lock (_gate)
            {
                return _tripped;
            }
        }
    }

    /// <summary>The exception a runner cancels with once the breakpoint tripped, null before.</summary>
    public BreakException? BreakError => Tripped is { Length: > 0 } reason ? new BreakException(reason) : null;

    /// <summary>
    /// The natural-pause hook's half of a tick or minute breakpoint (a stage breakpoint trips from Session.Stage):
    /// trips the ring when the run-phase offset or the game tick has reached the mark. The tick is read at most
    /// every BreakTickPoll.
    /// </summary>
    private async Task CheckBreakAsync(CancellationToken cancellationToken)
    {
        var b = Break;
        if (b.IsZero)
        {
            return;
        }

        if (b.Minute > TimeSpan.Zero)
        {
            TimeSpan offset;
            lock (_gate)
            {
                if (_tripped.Length > 0)
                {
                    return;
                }

                offset = OffsetLocked();
            }

            if (offset >= b.Minute)
            {
                Trip($"run phase reached {OffsetLabel(offset)}");
            }

            return;
        }

        if (b.Tick == 0)
        {
            return;
        }

        lock (_gate)
        {
            // The read below is itself a native call, and so a natural pause: _breakReading keeps it from
            // re-entering here.
            var now = Now();
            if (_tripped.Length > 0 || now < _nextBreakTick || _breakReading)
            {
                return;
            }

            _nextBreakTick = now + BreakTickPoll;
            _breakReading = true;
        }

        ulong? tick;
        try
        {
            tick = await ReadTickAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _breakReading = false;
            }
        }

        if (tick is { } reached && reached >= b.Tick)
        {
            Trip($"game tick {reached} reached {b.Tick}");
        }
    }

    /// <summary>
    /// The live game tick for a tick breakpoint: the service's /api/state while a service holds the game, an
    /// identity read on the harness while it does (a scenario clock's replies carry no tick), else the newest tick a
    /// reply carried (<see cref="ObservedTick"/>).
    /// </summary>
    private async Task<ulong?> ReadTickAsync(CancellationToken cancellationToken)
    {
        if (Service?.Invoke() is { } service)
        {
            return await ServiceTickAsync(service.ApiAsync, cancellationToken).ConfigureAwait(false);
        }

        if (Bridge?.Invoke() is { } harness)
        {
            try
            {
                var loaded = await ReadLoadedAsync(harness, "break-tick", cancellationToken).ConfigureAwait(false);
                var tick = loaded["context"].AsMap()?["tick"].AsNumber() ?? -1;
                return tick > 0 ? (ulong)tick : null;
            }
            catch (AcceptanceException)
            {
                return null;
            }
        }

        return ObservedTick?.Invoke();
    }

    /// <summary>
    /// Takes the bundle of a run paused at its breakpoint (BreakCheckpoint), pausing the game; unlike a periodic
    /// capture it is taken whether or not the ring is capped. It returns null, with the error on Errors, when no
    /// bundle could be taken.
    /// </summary>
    public async Task<Checkpoint?> BreakBundleAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _busy = true;
        }

        try
        {
            return await CaptureCoreAsync(Breakpoint.BreakCheckpoint, force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsCaptureFailure(e))
        {
            RecordError(Breakpoint.BreakCheckpoint, e.Message);
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }
}

/// <summary>Go duration text (Go <c>time.Duration.String</c> / <c>ParseDuration</c>), for specs and labels.</summary>
internal static partial class GoDuration
{
    public static bool TryParse(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        var s = text;
        var negative = false;
        if (s.Length > 0 && (s[0] == '+' || s[0] == '-'))
        {
            negative = s[0] == '-';
            s = s[1..];
        }

        if (s.Length == 0 || !FullPattern().IsMatch(s))
        {
            return false;
        }

        double ticks = 0;
        foreach (var m in PartPattern().EnumerateMatches(s))
        {
            var part = s.AsSpan(m.Index, m.Length);
            var split = part.IndexOfAnyExceptInRange('0', '9');
            while (split >= 0 && part[split] == '.')
            {
                var next = part[(split + 1)..].IndexOfAnyExceptInRange('0', '9');
                split = next < 0 ? -1 : split + 1 + next;
            }

            var number = double.Parse(part[..split], NumberStyles.Float, CultureInfo.InvariantCulture);
            var unit = part[split..] switch
            {
                "ns" => TimeSpan.TicksPerMillisecond / 1_000_000.0,
                "us" or "µs" => TimeSpan.TicksPerMillisecond / 1000.0,
                "ms" => TimeSpan.TicksPerMillisecond,
                "s" => TimeSpan.TicksPerSecond,
                "m" => TimeSpan.TicksPerMinute,
                _ => (double)TimeSpan.TicksPerHour,
            };
            ticks += number * unit;
        }

        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            return false;
        }

        value = TimeSpan.FromTicks((long)(negative ? -ticks : ticks));
        return true;
    }

    /// <summary>Formats like Go: "1m30s", "45s", "1h0m0s", "500ms", "1.5s".</summary>
    public static string Format(TimeSpan d)
    {
        if (d == TimeSpan.Zero)
        {
            return "0s";
        }

        var ticks = Math.Abs(d.Ticks);
        var b = new StringBuilder();
        if (d < TimeSpan.Zero)
        {
            b.Append('-');
        }

        if (ticks < TimeSpan.TicksPerSecond)
        {
            b.Append(Fraction(ticks, TimeSpan.TicksPerMillisecond)).Append("ms");
            return b.ToString();
        }

        var hours = ticks / TimeSpan.TicksPerHour;
        var minutes = ticks % TimeSpan.TicksPerHour / TimeSpan.TicksPerMinute;
        var secondTicks = ticks % TimeSpan.TicksPerMinute;
        if (hours > 0)
        {
            b.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('h');
        }

        if (hours > 0 || minutes > 0)
        {
            b.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('m');
        }

        b.Append(Fraction(secondTicks, TimeSpan.TicksPerSecond)).Append('s');
        return b.ToString();
    }

    private static string Fraction(long ticks, long unit)
    {
        var whole = ticks / unit;
        var rest = ticks % unit;
        if (rest == 0)
        {
            return whole.ToString(CultureInfo.InvariantCulture);
        }

        var digits = unit.ToString(CultureInfo.InvariantCulture).Length - 1;
        var fraction = rest.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0').TrimEnd('0');
        return whole.ToString(CultureInfo.InvariantCulture) + "." + fraction;
    }

    [GeneratedRegex(@"^(?:(?:\d+(?:\.\d*)?|\.\d+)(?:ns|us|µs|ms|s|m|h))+$")]
    private static partial Regex FullPattern();

    [GeneratedRegex(@"(?:\d+(?:\.\d*)?|\.\d+)(?:ns|us|µs|ms|s|m|h)")]
    private static partial Regex PartPattern();
}
