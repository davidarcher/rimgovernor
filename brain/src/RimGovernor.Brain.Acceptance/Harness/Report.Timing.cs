using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

// Every result.json carries the run's timing so a slower harness is visible without inferring runtimes from
// evidence mtimes: Create stamps started_at, Finalize adds finished_at, wall_ms, boot_ms (from the session open or
// game_reuse.openMs), launch_dir and boot_phases (the game's launch spans, see BootPhases), ticks_advanced (the game ticks the harness observed pass, see TickStats) and wall_tps.
public sealed partial class Report
{
    public const string StartedAtKey = "started_at";

    public const string FinishedAtKey = "finished_at";

    public const string WallMsKey = "wall_ms";

    public const string BootMsKey = "boot_ms";

    public const string TicksAdvancedKey = "ticks_advanced";

    public const string WallTpsKey = "wall_tps";

    /// <summary>The opened game's channel directory (<see cref="LaunchDirectory"/>), where <c>RimGovernor.cmd launch-phases</c> finds the run's traces.</summary>
    public const string LaunchDirKey = "launch_dir";

    /// <summary>
    /// The opened game's channel directory, where it writes its OTLP rings; Finalize reads <c>boot_phases</c>
    /// (<see cref="BootPhases"/>) from its traces when set and records it as <see cref="LaunchDirKey"/>.
    /// </summary>
    public string LaunchDirectory { get; set; } = "";

    /// <summary>Fills the timing fields at Finalize.</summary>
    internal void FinalizeTiming(DateTimeOffset finished)
    {
        this[FinishedAtKey] = finished.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        if (ParseReportTime(Data.Member(StartedAtKey)) is not { } started)
        {
            return;
        }

        var wall = finished - started;
        this[WallMsKey] = (long)wall.TotalMilliseconds;
        if (!Has(BootMsKey) && Data.Member("game_reuse").Member("openMs").AsUInt64() is { } openMs)
        {
            this[BootMsKey] = (long)openMs;
        }

        if (!Has(BootPhases.Key) && LaunchDirectory.Length > 0)
        {
            this[LaunchDirKey] = LaunchDirectory;
            try
            {
                this[BootPhases.Key] = BootPhases.Read(OtlpFiles.RingPath(LaunchDirectory, OtlpSignal.Traces), started);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this[BootPhases.Key + "_error"] = e.Message;
            }
        }

        var advanced = Data.Member(TicksAdvancedKey).AsUInt64();
        if (advanced is null)
        {
            advanced = TickStats.Advanced;
            this[TicksAdvancedKey] = (long)advanced.Value;
        }

        if (!Has(WallTpsKey) && wall > TimeSpan.Zero)
        {
            this[WallTpsKey] = advanced.Value / wall.TotalSeconds;
        }
    }

    internal static DateTimeOffset? ParseReportTime(JsonNode? value)
    {
        var text = value.AsString();
        if (text.Length == 0)
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }
}

/// <summary>
/// Accumulates the game ticks the process has seen pass (Go <c>tickStats</c>): every harness reply that carries the
/// game tick (an identity read, the typed colony facts, the supervised clock) feeds <see cref="Observe"/>, which
/// sums the forward deltas between consecutive observations. A load (<see cref="ResetBaseline"/>) starts a new
/// baseline so a save's tick is not counted as progress, and a rewind only re-baselines.
/// </summary>
public static class TickStats
{
    private static readonly object Gate = new();
    private static bool _seen;
    private static ulong _last;
    private static ulong _advanced;

    /// <summary>The game ticks observed to pass so far.</summary>
    public static ulong Advanced
    {
        get
        {
            lock (Gate)
            {
                return _advanced;
            }
        }
    }

    /// <summary>The newest tick a reply carried since the last load, or null before one did (Go <c>lastObservedTick</c>).</summary>
    public static ulong? Last
    {
        get
        {
            lock (Gate)
            {
                return _seen ? _last : null;
            }
        }
    }

    public static void Observe(ulong tick)
    {
        lock (Gate)
        {
            if (_seen && tick > _last)
            {
                _advanced += tick - _last;
            }

            _seen = true;
            _last = tick;
        }
    }

    public static void ResetBaseline()
    {
        lock (Gate)
        {
            _seen = false;
        }
    }

    /// <summary>Empties the process's tick count so a runner that executes several cases in one process reports each case's own.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _seen = false;
            _last = 0;
            _advanced = 0;
        }
    }

    /// <summary>Feeds <see cref="Observe"/> with the tick a reply carried and re-baselines after a load or start.</summary>
    public static void ObserveReply(string tool, ulong? tick)
    {
        if (tool is Harness.LoadGameReadyTool or "rimgovernor/lifecycle_load")
        {
            ResetBaseline();
            return;
        }

        if (tick is { } observed)
        {
            Observe(observed);
        }
    }

    /// <summary>
    /// The game tick a decoded reply carries, when it does: any <c>rimgovernor/*</c> wire reply whose payload is a
    /// lifecycle <c>loaded</c> context (an identity read, a load, an authority acquisition) or an <c>observed</c>
    /// snapshot context (the typed colony facts).
    /// </summary>
    public static ulong? ReplyTick(string tool, JsonObject? payload)
    {
        if (!tool.StartsWith("rimgovernor/", StringComparison.Ordinal) || payload is null)
        {
            return null;
        }

        var encoded = payload.Member("payload");
        if (encoded is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        return RecordedOutcome.Of(text) is ("loaded" or "observed", var body)
            && HarnessJson.TryRead(body, HarnessJsonContext.Default.ContextBody)?.Context?.Tick is long tick && tick >= 0
            ? (ulong)tick
            : null;
    }
}
