using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

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
        { "ticks", Ticks },
        { "first_tick", FirstTick },
        { "last_tick", LastTick },
        { "elapsed_ticks", Elapsed },
        { "tick_sampled", Sampled },
        { "reached", Reached },
        { "watch_wall", WatchWall },
    };
}
