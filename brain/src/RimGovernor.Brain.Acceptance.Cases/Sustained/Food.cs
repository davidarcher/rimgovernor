namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The baseline save, wall window and failure joining the long-window cases share (<c>sustained/colony</c>, the solo
/// cases and the sleeping bedroom cases).
/// </summary>
internal static class SustainedFoodCase
{
    /// <summary>The hand-prepared eight-colonist tribal start every serve-driven case loads unless it names another save.</summary>
    internal const string BaselineSave = "RimGovernor-tribal8-baseline";

    /// <summary>The sample window: wall-clock minutes at the serve clock speed, not ticks; the regression-gate length.</summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(8);

    /// <summary>The environment variable that overrides <see cref="DefaultWindow"/> with a Go duration (e.g. 20m).</summary>
    internal const string WindowEnv = "RIMGOVERNOR_ACCEPT_WINDOW";

    /// <summary><see cref="WindowEnv"/> when set and valid, else <see cref="DefaultWindow"/>.</summary>
    internal static TimeSpan Window()
    {
        var raw = Environment.GetEnvironmentVariable(WindowEnv);
        if (!string.IsNullOrEmpty(raw) && GoDuration.TryParse(raw, out var d) && d > TimeSpan.Zero)
        {
            return d;
        }

        return DefaultWindow;
    }

    /// <summary>The Go <c>errors.Join</c> of the failures that are not null; null when none failed.</summary>
    internal static AcceptanceException? Join(IEnumerable<Exception?> failures)
    {
        var messages = failures.Where(f => f is not null).Select(f => f!.Message).ToList();
        return messages.Count == 0 ? null : new AcceptanceException(string.Join('\n', messages));
    }

    /// <summary>Runs <paramref name="step"/> and returns its harness failure, null when it passed.</summary>
    internal static async Task<Exception?> CaptureAsync(Func<Task> step)
    {
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
