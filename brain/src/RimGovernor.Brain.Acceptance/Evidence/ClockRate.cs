using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The rate-based stall guard (#3350): over each window of wall time, the game tick must advance at least the floor's
/// ticks per second. A clock that stops, or crawls far below what any passing run reaches, ends the run within about
/// one window instead of at its wall budget. Time the harness spends on its own pauses (a checkpoint capture) is
/// shifted out of the window.
/// </summary>
public sealed class ClockRate
{
    /// <summary>
    /// The default floor: half of Normal speed (60 ticks/s) and a tenth of the hang guard's slow-machine speed
    /// (<see cref="TypedCase.HangGuardTicksPerSecond"/>). A served or harness-run clock runs at Superfast or faster
    /// (hundreds of ticks a second), so only a held or starved clock falls below it for a whole window.
    /// </summary>
    public const double DefaultFloor = 30;

    private readonly double _floor;
    private readonly TimeSpan _window;
    private TimeSpan _anchorAt;
    private ulong _anchorTick;
    private bool _anchored;
    private TimeSpan _started;
    private bool _begun;
    private ulong _last;

    /// <summary>A guard of <paramref name="floor"/> ticks per second measured over <paramref name="window"/>.</summary>
    public ClockRate(double floor, TimeSpan window)
    {
        _floor = floor;
        _window = window;
    }

    /// <summary>
    /// Folds one reading taken <paramref name="now"/> into the guard (<paramref name="tick"/> null when it was
    /// unreadable) and returns the failure once a whole window fell below the floor, else null. A window that met the
    /// floor starts the next one at this reading.
    /// </summary>
    public string? Observe(ulong? tick, TimeSpan now)
    {
        if (!_begun)
        {
            _begun = true;
            _started = now;
        }

        if (tick is { } read)
        {
            _last = read;
            if (!_anchored)
            {
                _anchored = true;
                _anchorTick = read;
                _anchorAt = now;
                return null;
            }
        }
        else if (!_anchored)
        {
            return now - _started >= _window
                ? $"game tick unreadable for {WaitError.FormatDuration(now - _started)}: the game is not running or has no map loaded"
                : null;
        }

        var span = now - _anchorAt;
        if (span < _window)
        {
            return null;
        }

        var advanced = _last > _anchorTick ? _last - _anchorTick : 0;
        var rate = advanced / span.TotalSeconds;
        if (rate < _floor)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"clock advanced {advanced} ticks in {WaitError.FormatDuration(span)} from tick {_anchorTick} ({rate:0.##} ticks/s, below the case's floor of {_floor:0.##} ticks/s)");
        }

        _anchorTick = _last;
        _anchorAt = now;
        return null;
    }

    /// <summary>Moves the window past a deliberate pause of <paramref name="paused"/> (a checkpoint capture).</summary>
    public void Shift(TimeSpan paused)
    {
        _anchorAt += paused;
        _started += paused;
    }

    /// <summary>
    /// The clock as <c>clock_read_status</c> reports it for <paramref name="identity"/>: its state, hold reason and
    /// detail, target speed and effective rate, what a slow-clock failure names as its cause. A failed read is described,
    /// not thrown: the slow clock stays the failure.
    /// </summary>
    public static async Task<string> DescribeAsync(Harness h, Common.Identity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        try
        {
            var status = await h.CallAsync("clock-rate-status", NativeOps.ReadClockStatus, new Clock.StatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
            return Describe(status);
        }
        catch (BridgeRefusedException e)
        {
            return "clock status refused: " + e.Message;
        }
        catch (Exception e) when (e is AcceptanceException or BridgeUnavailableException)
        {
            return "clock status unreadable: " + e.Message;
        }
    }

    /// <summary>The status as one line: held with its reason and detail, or running with its pacing.</summary>
    public static string Describe(Clock.Status status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var paused = status.ActualPaused == true ? "paused" : "unpaused";
        var common = string.Create(CultureInfo.InvariantCulture, $"target {status.TargetSpeed?.ToString() ?? "none"}, {status.EffectiveTicksPerSecond ?? 0:0.#} ticks/s effective, game {paused}");
        return status.State == Clock.ClockState.Held
            ? $"clock held ({status.Reason}): {status.Detail}; {common}"
            : string.Create(CultureInfo.InvariantCulture, $"clock {status.State} paced at {status.PacedTicksPerSecond ?? 0} ticks/s ({status.PacingReason}); {common}");
    }
}
