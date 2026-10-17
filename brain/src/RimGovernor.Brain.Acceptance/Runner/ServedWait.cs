using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The wait under a clock the harness does not own: the brain service runs the game, and the harness only observes.
/// <see cref="TypedSession.ServeUntilAsync"/> runs it while the declared service drives the clock. The budget is game
/// ticks read from the game itself, so it means the same at any speed; the wait's stall and terminal (the service's
/// exit) end a run that stops moving, and the case's hang guard is the wall-clock backstop.
/// </summary>
public static class ServedWait
{
    /// <summary>
    /// Polls <paramref name="probe"/> under <paramref name="wait"/>'s stall, terminal and interval until it is done, and
    /// fails once the game has advanced more than <paramref name="ticks"/> ticks past the first reading of
    /// <paramref name="tick"/>, including a probe that finishes only after the budget ran out. Returns the ticks the game
    /// advanced.
    /// </summary>
    public static async Task<int> UntilAsync(string label, int ticks, Func<CancellationToken, Task<ulong>> tick, Probe probe, Wait wait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(wait);
        ulong? first = null;
        ulong last = 0;
        try
        {
            await (wait with
            {
                Ticks = (ulong)Math.Max(ticks, 0),
                Tick = async ct =>
                {
                    last = await tick(ct).ConfigureAwait(false);
                    first ??= last;
                    return last;
                },
            }).WaitProgressAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }

        var ran = first is { } start && last > start ? last - start : 0;
        if (ran > (ulong)Math.Max(ticks, 0))
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: done only after {ran} ticks, past the wait's {ticks}"));
        }

        return (int)ran;
    }
}
