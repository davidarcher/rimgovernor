namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The baseline save, wall window and failure joining the long-window runs share (<c>acceptance colony</c> and the
/// sleeping bedroom cases, which load the save).
/// </summary>
public static class SustainedFoodCase
{
    /// <summary>The hand-prepared eight-colonist tribal start every serve-driven case loads unless it names another save.</summary>
    public const string BaselineSave = "RimGovernor-tribal8-baseline";

    /// <summary>The sample window: wall-clock minutes at the serve clock speed, not ticks; the regression-gate length.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(8);

    /// <summary>The environment variable that overrides <see cref="DefaultWindow"/> with a duration (e.g. 20m).</summary>
    public const string WindowEnv = "RIMGOVERNOR_ACCEPT_WINDOW";

    /// <summary><see cref="WindowEnv"/> when set and valid, else <see cref="DefaultWindow"/>.</summary>
    public static TimeSpan Window()
    {
        var raw = Environment.GetEnvironmentVariable(WindowEnv);
        if (!string.IsNullOrEmpty(raw) && GoDuration.TryParse(raw, out var d) && d > TimeSpan.Zero)
        {
            return d;
        }

        return DefaultWindow;
    }

    /// <summary>The failures that are not null joined into one; null when none failed.</summary>
    public static AcceptanceException? Join(IEnumerable<Exception?> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var messages = failures.Where(f => f is not null).Select(f => f!.Message).ToList();
        return messages.Count == 0 ? null : new AcceptanceException(string.Join('\n', messages));
    }

    /// <summary>Runs <paramref name="step"/> and returns its harness failure, null when it passed.</summary>
    public static async Task<Exception?> CaptureAsync(Func<Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            await step().ConfigureAwait(false);
            return null;
        }
        catch (AcceptanceException e)
        {
            return e;
        }
    }
}
