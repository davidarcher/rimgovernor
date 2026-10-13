using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Reports the wait's current progress signature and whether it is done. The signature is whatever the wait counts
/// as progress: a plan lineage's stages, a review revision, a colony fact. Include the game tick only when the wait
/// tolerates a plan that is not moving while the game runs; leave it out when the plan itself is what must move.
/// </summary>
public delegate Task<(string Signature, bool Done)> Probe(CancellationToken cancellationToken);

/// <summary>Says which bound ended a <see cref="Wait.WaitProgressAsync"/> call.</summary>
public enum WaitOutcome
{
    Stalled = 1,
    Ceiling,
    Terminal,
    Ticks,
}

/// <summary>The failure <see cref="Wait.WaitProgressAsync"/> raises for its own bounds; a probe or cancellation error propagates as is.</summary>
public sealed class WaitError : AcceptanceException
{
    public WaitError(WaitOutcome outcome, string signature, TimeSpan quiet, TimeSpan elapsed, int rounds, Exception? cause, ulong ticksElapsed)
        : base(Describe(outcome, signature, quiet, elapsed, rounds, cause, ticksElapsed), cause)
    {
        Outcome = outcome;
        Signature = signature;
        Quiet = quiet;
        Elapsed = elapsed;
        Rounds = rounds;
        Cause = cause;
        TicksElapsed = ticksElapsed;
    }

    public WaitOutcome Outcome { get; }

    /// <summary>The last signature observed.</summary>
    public string Signature { get; }

    /// <summary>How long the signature had been unchanged.</summary>
    public TimeSpan Quiet { get; }

    /// <summary>The whole wait.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The probes made.</summary>
    public int Rounds { get; }

    /// <summary>The Terminal check's error for <see cref="WaitOutcome.Terminal"/>.</summary>
    public Exception? Cause { get; }

    /// <summary>How far the game advanced during the wait, when the wait had a Tick reader.</summary>
    public ulong TicksElapsed { get; }

    /// <summary>A duration rounded to whole seconds and written as Go prints it ("1m30s", "0s").</summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        var seconds = (long)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero);
        var text = new StringBuilder();
        if (seconds < 0)
        {
            text.Append('-');
            seconds = -seconds;
        }

        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        if (hours > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"{hours}h");
        }

        if (hours > 0 || minutes > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"{minutes}m");
        }

        text.Append(CultureInfo.InvariantCulture, $"{seconds % 60}s");
        return text.ToString();
    }

    private static string Describe(WaitOutcome outcome, string signature, TimeSpan quiet, TimeSpan elapsed, int rounds, Exception? cause, ulong ticksElapsed)
    {
        var quietText = FormatDuration(quiet);
        var elapsedText = FormatDuration(elapsed);
        return outcome switch
        {
            WaitOutcome.Terminal => $"wait ended: {cause?.Message} (after {elapsedText}, last signature \"{signature}\")",
            WaitOutcome.Ticks => $"wait tick budget spent: {ticksElapsed} ticks of game time (signature \"{signature}\" unchanged for {quietText}, {elapsedText} elapsed, {rounds} probes)",
            WaitOutcome.Ceiling => $"wait ceiling {elapsedText} reached (signature \"{signature}\" unchanged for {quietText}, {rounds} probes)",
            _ => $"wait stalled: signature \"{signature}\" unchanged for {quietText} ({elapsedText} elapsed, {rounds} probes)",
        };
    }
}

/// <summary>
/// Bounds a poll loop two ways: a ceiling on the whole wait and, tighter, a stall budget on the time since the
/// probe's signature last changed. A broken run stops changing long before it reaches the ceiling, so the stall
/// budget is what ends it; the ceiling is the safety net for a run that keeps changing without ever finishing.
/// </summary>
public sealed record Wait
{
    /// <summary>Overrides the default stall budget (a duration such as "90s") for the shared waits and for harnesses whose -stall flag defaults to <see cref="StallBudget"/>.</summary>
    public const string StallEnv = "RIMGOVERNOR_ACCEPT_STALL";

    /// <summary>RimWorld's game day, for tick budgets stated in days.</summary>
    public const int TicksPerDay = 60000;

    /// <summary>RimWorld's in-game hour.</summary>
    public const int TicksPerHour = 2500;

    /// <summary>
    /// The shared stall budget: long enough for a wall segment or a haul under the clock, short enough that a lost
    /// service or a plan the executor never moves ends the run soon after it stops moving. Across 406 passing rows
    /// the longest quiet span was p90 6s, p99 39s; a case whose passing runs hold a signature longer declares its
    /// own stall rather than inflating this. A wait that needs the game to do more than a minute of work is bounded
    /// in ticks (<see cref="Ticks"/>), not by a longer stall. Finalize records each run's longest quiet span under
    /// wait_stats so the budget can be tuned from passing runs.
    /// </summary>
    public static readonly TimeSpan DefaultStall = TimeSpan.FromMinutes(1);

    /// <summary>The whole-wait budget; zero means only the stall budget and the token bound the wait.</summary>
    public TimeSpan Ceiling { get; init; }

    /// <summary>The budget for the signature not changing; zero disables stall detection.</summary>
    public TimeSpan Stall { get; init; }

    /// <summary>The pause between probes (default 2s).</summary>
    public TimeSpan Interval { get; init; }

    /// <summary>
    /// Checked before every probe; a non-null result ends the wait immediately (a game process that exited, an
    /// interrupted scenario, a plan in a failed state).
    /// </summary>
    public Func<Exception?>? Terminal { get; init; }

    /// <summary>
    /// The game-time budget, with <see cref="Tick"/> set: the wait fails once the game has advanced more than this
    /// many ticks past the tick reported at the first probe. A wait for something the game itself must do (a haul,
    /// a surgery, a pen) is bounded this way, so the bound means the same at every speed and on every machine;
    /// Stall still catches a game that stops ticking (a pausing letter) and Ceiling a run that keeps ticking
    /// without finishing.
    /// </summary>
    public ulong Ticks { get; init; }

    /// <summary>Reads the current game tick.</summary>
    public Func<CancellationToken, Task<ulong>>? Tick { get; init; }

    /// <summary><see cref="StallEnv"/>'s duration, or <see cref="DefaultStall"/> when unset or unparsable.</summary>
    public static TimeSpan StallBudget()
    {
        if (Environment.GetEnvironmentVariable(StallEnv) is { Length: > 0 } text && TryParseDuration(text, out var parsed) && parsed > TimeSpan.Zero)
        {
            return parsed;
        }

        return DefaultStall;
    }

    /// <summary>Joins parts into a progress signature for a <see cref="Probe"/>.</summary>
    public static string Signature(params object?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return string.Join("|", parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture) ?? ""));
    }

    /// <summary>Polls <paramref name="probe"/> under this wait's bounds until it reports done; throws a <see cref="WaitError"/> when a bound ends the wait.</summary>
    public async Task WaitProgressAsync(Probe probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var interval = Interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : Interval;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var start = TimeSpan.Zero;
        var last = "";
        var lastChange = TimeSpan.Zero;
        var rounds = 0;
        ulong startTick = 0;
        ulong ticksElapsed = 0;

        // The longest the signature stayed unchanged, including the final span when the wait ends; Finalize reports the run's maximum.
        var longest = TimeSpan.Zero;
        var stalled = false;
        try
        {
            while (true)
            {
                var now = clock.Elapsed;
                if (Terminal?.Invoke() is { } cause)
                {
                    throw new WaitError(WaitOutcome.Terminal, last, now - lastChange, now - start, rounds, cause, ticksElapsed);
                }

                if (Tick is not null)
                {
                    var tick = await Tick(cancellationToken).ConfigureAwait(false);
                    if (rounds == 0)
                    {
                        startTick = tick;
                    }

                    if (tick > startTick)
                    {
                        ticksElapsed = tick - startTick;
                    }
                }

                var (signature, done) = await probe(cancellationToken).ConfigureAwait(false);
                rounds++;
                if (done)
                {
                    return;
                }

                now = clock.Elapsed;
                if (rounds == 1 || signature != last)
                {
                    if (now - lastChange > longest)
                    {
                        longest = now - lastChange;
                    }

                    last = signature;
                    lastChange = now;
                }

                var quiet = now - lastChange;
                if (Ticks > 0 && Tick is not null && ticksElapsed > Ticks)
                {
                    throw new WaitError(WaitOutcome.Ticks, last, quiet, now - start, rounds, null, ticksElapsed);
                }

                if (Stall > TimeSpan.Zero && quiet >= Stall)
                {
                    stalled = true;
                    throw new WaitError(WaitOutcome.Stalled, last, quiet, now - start, rounds, null, ticksElapsed);
                }

                if (Ceiling > TimeSpan.Zero && now - start >= Ceiling)
                {
                    throw new WaitError(WaitOutcome.Ceiling, last, quiet, now - start, rounds, null, ticksElapsed);
                }

                // Between probes is a natural pause for the checkpoint ring; the time a capture takes is not the wait's quiet time.
                var took = await Checkpoints.PauseAsync(cancellationToken).ConfigureAwait(false);
                if (took > TimeSpan.Zero)
                {
                    start += took;
                    lastChange += took;
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            var quietNow = clock.Elapsed - lastChange;
            if (quietNow > longest)
            {
                longest = quietNow;
            }

            WaitStatistics.Record(longest, last, stalled);
        }
    }

    // Go duration text ("90s", "1m30s", "250ms", "1h") or a bare number of seconds.
    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var total = 0.0;
        var i = 0;
        while (i < text.Length)
        {
            var numberStart = i;
            while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
            {
                i++;
            }

            if (i == numberStart || !double.TryParse(text.AsSpan(numberStart, i - numberStart), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            var unitStart = i;
            while (i < text.Length && !char.IsAsciiDigit(text[i]) && text[i] != '.')
            {
                i++;
            }

            var factor = text[unitStart..i] switch
            {
                "ms" => 0.001,
                "s" => 1.0,
                "m" => 60.0,
                "h" => 3600.0,
                _ => -1.0,
            };
            if (factor < 0)
            {
                return false;
            }

            total += number * factor;
        }

        duration = TimeSpan.FromSeconds(total);
        return true;
    }
}
