using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Reports a game whose live tick has stopped advancing (or was never read) for longer than the limit: the game died,
/// or it was relaunched to the main menu with no map loaded (Go <c>sustainedfood.tickStall</c>).
/// </summary>
internal sealed class TickStall
{
    private readonly TimeSpan _limit;
    private ulong _last;
    private bool _seen;
    private DateTimeOffset _advanced;
    private bool _noWork;

    internal TickStall(TimeSpan limit, DateTimeOffset now)
    {
        _limit = limit;
        _advanced = now;
    }

    internal void Observe(ulong tick, DateTimeOffset now)
    {
        if (!_seen || tick != _last)
        {
            _last = tick;
            _seen = true;
            _advanced = now;
            _noWork = false;
        }
    }

    /// <summary>Records a scheduler admission refusal naming no_work.</summary>
    internal void RefusedNoWork() => _noWork = true;

    /// <summary>
    /// Reports a parked game: the scheduler refused to run the clock for lack of work and the tick has not moved for
    /// <paramref name="grace"/>. Nothing can change in a paused game the governor will not resume, so the window ends
    /// and the case's audit judges the frozen state instead of waiting out the watch.
    /// </summary>
    internal bool Idle(DateTimeOffset now, TimeSpan grace) => _noWork && _seen && grace > TimeSpan.Zero && now - _advanced >= grace;

    /// <summary>Restarts the clock after a deliberate pause (a checkpoint save).</summary>
    internal void Reset(DateTimeOffset now) => _advanced = now;

    /// <summary>The failure once the tick has been stuck past the limit, null before.</summary>
    internal AcceptanceException? Check(DateTimeOffset now)
    {
        if (_limit <= TimeSpan.Zero || now - _advanced <= _limit)
        {
            return null;
        }

        var limit = WaitError.FormatDuration(_limit);
        return !_seen
            ? new AcceptanceException($"game tick unreadable for {limit}: the game is not running or has no map loaded")
            : new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"game tick stuck at {_last} for {limit}: the game died or was relaunched with no map loaded"));
    }
}

/// <summary>
/// The report's record of the tick-measured watch window (<c>report["window"]</c>): the configured length, the first
/// and last live ticks the watch sampled, how many ticks elapsed between them, whether the configured length was
/// reached before the wall-clock ceiling, and how long the watch ran on the wall clock. <see cref="Ticks"/> is 0 when
/// the run watched on wall-clock alone, in which case <see cref="Reached"/> stays false.
/// </summary>
public sealed class TickWindow
{
    public TickWindow(ulong ticks) => Ticks = ticks;

    public ulong Ticks { get; }

    public ulong FirstTick { get; private set; }

    public ulong LastTick { get; private set; }

    public ulong Elapsed { get; private set; }

    public bool Sampled { get; private set; }

    public bool Reached { get; set; }

    public string WatchWall { get; set; } = "";

    /// <summary>Folds one live tick into the window; the first observation anchors it and a tick below the anchor (a rewind) is ignored.</summary>
    public void Observe(ulong tick)
    {
        if (!Sampled)
        {
            Sampled = true;
            FirstTick = tick;
        }

        if (tick >= FirstTick)
        {
            LastTick = tick;
            Elapsed = tick - FirstTick;
        }
    }

    /// <summary>Whether a configured tick window has elapsed.</summary>
    public bool HasReached() => Ticks > 0 && Sampled && Elapsed >= Ticks;

    /// <summary>The window in the report's JSON shape.</summary>
    public JsonObject ToJson() => new()
    {
        ["ticks"] = Ticks,
        ["first_tick"] = FirstTick,
        ["last_tick"] = LastTick,
        ["elapsed_ticks"] = Elapsed,
        ["tick_sampled"] = Sampled,
        ["reached"] = Reached,
        ["watch_wall"] = WatchWall,
    };
}
