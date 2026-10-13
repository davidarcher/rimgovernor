namespace RimGovernor.Brain.Domain;

/// <summary>
/// The game's calendar (GenDate): the one place the brain states it, because policy constants are
/// compile-time. The catalog's GameConstants carry the game's own values and bridge refuses a catalog whose
/// calendar differs, so these never disagree with the game.
/// </summary>
public static class GameTime
{
    public const int TicksPerHour = 2500;
    public const int TicksPerDay = 60000;
    public const int DaysPerYear = 60;

    /// <summary>GenDate.TicksPerYear (a year is <see cref="DaysPerYear"/> days).</summary>
    public const int TicksPerYear = TicksPerDay * DaysPerYear;
}
