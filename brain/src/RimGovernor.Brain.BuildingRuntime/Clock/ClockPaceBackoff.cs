namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_pace_backoff.go.
//
// Player acceleration's controller side. Native paces a PACING_PLAYER_ACCELERATED window up to the boosted rate; the
// controller keeps its critical evidence (the admission cycle's critical wave) current by lowering the window's
// tick-rate ceiling before that evidence can age past the safe horizon, rather than letting the window run blind
// and then stop and readmit at full speed.
//
// Each critical wave under a running window is watched: the wave's evidence was read at a known wall time, and at
// the window's worst-case rate (its ceiling, or the boosted rate without one) it ages past the horizon after
// horizon/rate seconds (the watch fires at StaleShare of that). If the wave has not completed by then the evidence
// is actually stale, and the backoff lowers the ceiling to Normal at once. (Before the live choppiness fix the watch
// fired at half the horizon, so every ordinary 1.5-2.5 s wave under a 300-tick horizon collapsed the ceiling to
// Normal and the rate sawtoothed 60<->100 about once a second.) A completed wave is fresh evidence: its wall time
// sets the rate at which the next wave fits inside the margin (half the horizon), and the ceiling moves toward it
// with hysteresis: a change under HysteresisShare of the current ceiling is ignored, and fresh changes come at most
// once per HysteresisInterval; up at most RiseFactor times, and released (full speed) only when the target reaches
// the boosted rate. A full-speed request is never issued while a wave's evidence is stale, and a stale wave's drop
// to Normal bypasses the hysteresis.

/// <summary>
/// One scheduler's ceiling state (Go <c>paceBackoff</c>). The request issues an owned speed change on the running
/// epoch the last watched wave saw, carrying the ceiling (<see cref="ReleaseTicksPerSecond"/> releases); its failure
/// leaves the recorded ceiling as it was, so the next decision retries.
/// </summary>
internal sealed class PaceBackoff
{
    /// <summary>The boosted rate native's player pacing climbs to (150 x Normal); a ceiling at or above it is full speed.</summary>
    public const uint FullTicksPerSecond = 9000;

    /// <summary>Normal: where stale evidence parks the window. The window keeps advancing, it only slows.</summary>
    public const uint FloorTicksPerSecond = 60;

    /// <summary>The wire's largest ceiling: the request that releases a lowered ceiling.</summary>
    public const uint ReleaseTicksPerSecond = 60000;

    /// <summary>
    /// The safe horizon without a configured one (live play runs without a blind-tick budget). The hazard bounds are
    /// native's every-tick safety check (every tick of an accelerated epoch runs it), not planner evidence age, so
    /// the default only has to keep planning roughly current: ~40 game seconds. 300 made every ordinary wave outrun
    /// the margin at 60-100 ticks/s.
    /// </summary>
    public static readonly Tick DefaultHorizonTicks = new(2500);

    // The share of the horizon a critical wave may span.
    private const double Margin = 0.5;

    // The share of the horizon a watched wave's evidence may age before it counts as stale: short of the whole
    // horizon so the Normal ceiling lands (one native round trip) before it is crossed.
    private const double StaleShare = 0.8;

    // The smallest relative ceiling change a fresh wave issues; smaller targets keep the current ceiling.
    private const double HysteresisShare = 0.25;

    // The least wall time between fresh-wave ceiling changes; stale drops ignore it. Waves arrive every 6-20 s
    // live, so a longer interval made each one a no-op.
    private static readonly TimeSpan HysteresisInterval = TimeSpan.FromSeconds(1);

    // How far one fresh wave raises the ceiling at most. A stale drop to Normal climbed one doubling per wave live,
    // so the sawtooth spent 40-100 s regaining the rate a wave earns in two.
    private const uint RiseFactor = 4;

    private readonly Lock _mu = new();
    private readonly Tick _horizon;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<PbClock.Epoch, uint, CancellationToken, Task> _request;

    // The rate the window was last set to, 0 for none (full).
    private uint _ceiling;

    // Set while a watched wave's evidence has aged past the margin; cleared when the wave completes.
    private bool _stale;

    // The wall time of the last recorded change.
    private DateTimeOffset _changedAt;

    // The running epoch the last watched wave saw; the ceiling change names it.
    private PbClock.Epoch? _epoch;

    public PaceBackoff(Tick horizon, Func<DateTimeOffset> now, Func<PbClock.Epoch, uint, CancellationToken, Task> request)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(request);
        _horizon = horizon.Value <= 0 ? DefaultHorizonTicks : horizon;
        _now = now;
        _request = request;
    }

    /// <summary>
    /// The ceiling a new window starts under: the rate the last waves earned, so recovery from a stop resumes it
    /// instead of restarting at full speed and backing off again. 0 is none.
    /// </summary>
    public uint Ceiling
    {
        get
        {
            lock (_mu)
            {
                return _ceiling;
            }
        }
    }

    /// <summary>The epoch a ceiling change is issued against (Go <c>paceEpoch</c>).</summary>
    public PbClock.Epoch? Epoch
    {
        get
        {
            lock (_mu)
            {
                return _epoch;
            }
        }
        set
        {
            lock (_mu)
            {
                _epoch = value;
            }
        }
    }

    // The fastest the window may run now. Callers hold the lock.
    private uint WorstRate() => _ceiling == 0 ? FullTicksPerSecond : _ceiling;

    /// <summary>
    /// Arms the backoff for one critical wave whose evidence was read at <paramref name="readAt"/>; the token scopes
    /// the requests the watch issues. The returned delegate reports the wave's end: completed with its wall time, or
    /// not (cut off, failed), which leaves the stale decision standing.
    /// </summary>
    public Func<TimeSpan, bool, Task> Watch(DateTimeOffset readAt, CancellationToken ct)
    {
        uint rate;
        lock (_mu)
        {
            rate = WorstRate();
        }
        var budget = TimeSpan.FromSeconds(_horizon.Value * StaleShare / rate);
        var fire = budget - (_now() - readAt);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(fire < TimeSpan.Zero ? TimeSpan.Zero : fire, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                await MarkStaleAsync(ct).ConfigureAwait(false);
            },
            CancellationToken.None);
        var done = 0;
        return async (wave, completed) =>
        {
            if (Interlocked.Exchange(ref done, 1) != 0)
            {
                return;
            }
            await stop.CancelAsync().ConfigureAwait(false);
            await finished.ConfigureAwait(false);
            stop.Dispose();
            if (completed)
            {
                await FreshAsync(wave, ct).ConfigureAwait(false);
            }
        };
    }

    // Lowers the ceiling to the floor: the wave outlived the margin.
    private async Task MarkStaleAsync(CancellationToken ct)
    {
        lock (_mu)
        {
            _stale = true;
            if (_ceiling != 0 && _ceiling <= FloorTicksPerSecond)
            {
                return;
            }
        }
        await SetAsync(FloorTicksPerSecond, "stale", ct).ConfigureAwait(false);
    }

    // Moves the ceiling toward the rate at which a wave of this wall time fits inside the margin.
    private async Task FreshAsync(TimeSpan wave, CancellationToken ct)
    {
        uint current;
        bool wait;
        lock (_mu)
        {
            _stale = false;
            current = _ceiling;

            // Hysteresis (b): leaving Normal after a stale drop is exempt, so a recovered wave does not park the
            // window for the whole interval.
            wait = current > FloorTicksPerSecond && _changedAt != default && _now() - _changedAt < HysteresisInterval;
        }
        if (wait)
        {
            return;
        }
        if (wave < TimeSpan.FromMilliseconds(1))
        {
            wave = TimeSpan.FromMilliseconds(1);
        }
        var target = (uint)Math.Min(ReleaseTicksPerSecond, _horizon.Value * Margin / wave.TotalSeconds);
        target = Math.Max(target, FloorTicksPerSecond);
        if (current != 0 && target != FloorTicksPerSecond && target < FullTicksPerSecond)
        {
            var diff = (double)target - current;
            if (diff > -HysteresisShare * current && diff < HysteresisShare * current)
            {
                return;
            }
        }
        if (current == 0)
        {
            if (target < FullTicksPerSecond)
            {
                await SetAsync(target, "wave", ct).ConfigureAwait(false);
            }
        }
        else if (target < current)
        {
            await SetAsync(target, "wave", ct).ConfigureAwait(false);
        }
        else if (target > current)
        {
            var next = Math.Min(target, RiseFactor * current);
            if (next >= FullTicksPerSecond)
            {
                await SetAsync(0, "fresh", ct).ConfigureAwait(false);
                return;
            }
            if (next > current)
            {
                await SetAsync(next, "fresh", ct).ConfigureAwait(false);
            }
        }
    }

    // Issues the change (0 releases) and records it once native took it. A release is refused outright while any
    // wave's evidence is stale.
    private async Task SetAsync(uint ceiling, string why, CancellationToken ct)
    {
        PbClock.Epoch? epoch;
        lock (_mu)
        {
            if (ceiling == 0 && _stale)
            {
                return;
            }
            epoch = _epoch;
        }
        var wire = ceiling == 0 ? ReleaseTicksPerSecond : ceiling;
        Exception? error = null;
        try
        {
            if (epoch is null)
            {
                throw new HeldException();
            }
            await _request(epoch, wire, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = e;
        }
        Tel.Decide(new Decision
        {
            Kind = "pace",
            Component = "clock-scheduler",
            Verdict = error is null ? "applied" : "failed",
            Reason = why,
            Target = "backoff",
            Attrs = new Dictionary<string, object?> { ["ceiling"] = wire, ["horizon_ticks"] = _horizon.Value, ["error"] = error?.Message },
        });
        if (error is not null)
        {
            return;
        }
        lock (_mu)
        {
            _ceiling = ceiling;
            _changedAt = _now();
        }
    }
}

/// <summary>
/// The pace a step's clock status reported: the running epoch's pacing reason, the rate it holds and its ceiling, and
/// native's effective speed (ticks per wall second, pauses included) (Go <c>StepPacing</c>).
/// </summary>
public sealed record StepPacing
{
    public string Reason { get; init; } = "";

    public uint PacedTps { get; init; }

    public uint Ceiling { get; init; }

    public double EffectiveTps { get; init; }

    public bool Player { get; init; }

    /// <summary>Reads the pace out of a status (Go <c>stepPacing</c>).</summary>
    public static StepPacing Of(PbClock.Status status)
    {
        var pacing = new StepPacing { EffectiveTps = status.HasEffectiveTicksPerSecond ? status.EffectiveTicksPerSecond : 0 };
        if (status.StateCase == PbClock.Status.StateOneofCase.Running && status.Running.Epoch is { } epoch)
        {
            pacing = pacing with
            {
                PacedTps = epoch.PacedTicksPerSecond,
                Ceiling = epoch.MaxTicksPerSecond,
                Player = epoch.HasPacing && epoch.Pacing == PbClock.Pacing.PlayerAccelerated,
                Reason = epoch.HasPacingReason ? PacingReasonName(epoch.PacingReason) : "",
            };
        }
        return pacing;
    }

    /// <summary>The wire reason without its enum prefix, lower case: <c>frame_budget</c>, <c>ceiling</c>, ... (Go <c>PacingReasonName</c>).</summary>
    public static string PacingReasonName(PbClock.PacingReason reason)
    {
        var name = reason.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }

    /// <summary>Adds the pace to a clock_step row: what the pacing reason and effective speed read (Go <c>publish</c>).</summary>
    public void Publish(IDictionary<string, object?> extra)
    {
        if (Reason.Length != 0)
        {
            extra["pacing_reason"] = Reason;
            extra["paced_tps"] = PacedTps;
        }
        if (Ceiling != 0)
        {
            extra["tps_ceiling"] = Ceiling;
        }
        if (Player)
        {
            extra["player_pacing"] = true;
        }
        if (EffectiveTps > 0)
        {
            extra["effective_tps"] = EffectiveTps;
        }
    }
}
