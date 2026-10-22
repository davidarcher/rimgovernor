namespace RimGovernor.Brain.Domain;

/// <summary>Native interval callbacks under a held update rate, including the retained tick counter
/// before the first callback. The counter or hash alignment can trigger that first callback.</summary>
public readonly record struct TickIntervalClock(int Interval, int Phase, int Elapsed)
{
    private int First => Math.Max(1, Math.Min(Interval - Elapsed, Phase == 0 ? Interval : Phase));
    public int AtOrAfter(int tick) => tick <= First ? First
        : tick + (Phase - tick % Interval + Interval) % Interval;
    public int ElapsedAt(int tick) => tick < First ? Elapsed + tick
        : tick - Math.Max(First, tick - (tick - Phase + Interval) % Interval);
    public (int Tick, int Delta) Next(int tick)
    {
        var next = AtOrAfter(tick + 1);
        return (next, next - tick + ElapsedAt(tick));
    }
}