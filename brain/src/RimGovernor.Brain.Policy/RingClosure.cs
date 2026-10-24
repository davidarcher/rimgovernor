namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one ring-closure estimate (epic #3738 decision D6): rings close in the order the builders work them, the k-th at
/// the cumulative ring work of rings 1..k over the builders, and a room's value accrues from its closure to the horizon.
/// The bed merge prices a room's size with it (#3748) and the portfolio forecast projects an unfinished ring's closure
/// with it (#3744).
/// </summary>
public static class RingClosure
{
    /// <summary>The tick a ring closes at: the cumulative ring work up to and including it (ticks) over the builders.</summary>
    public static double ClosedAt(double cumulativeWork, int builders) => cumulativeWork / builders;

    /// <summary>The days a closed room's value accrues: from its closure tick to the horizon, zero at or past it.</summary>
    public static double CreditDays(double horizonDays, double closedAt) => Math.Max(0, horizonDays - (closedAt / GameTime.TicksPerDay));
}
