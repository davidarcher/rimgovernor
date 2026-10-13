using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The fail-fast verdict when a run's step-stall budget elapsed without any scheduler step admitting a clock
/// window. It names the family selection the service ran with and the last step failure the clock worker recorded,
/// so a starved step budget reads as such instead of as twenty minutes of an unchanged EnsureFoodSupply timeline.
/// </summary>
public sealed class StepStallException : AcceptanceException
{
    public StepStallException(TimeSpan stall, string families, string lastFailure)
        : base(Describe(stall, families, lastFailure))
    {
        Stall = stall;
        Families = families;
        LastFailure = lastFailure;
    }

    public TimeSpan Stall { get; }

    public string Families { get; }

    /// <summary>The last WARN or ERROR worker-step planner_step row of the service's OTLP log ring, rendered by <see cref="StepStall.StepFailureText"/>; empty when it recorded none.</summary>
    public string LastFailure { get; }

    private static string Describe(TimeSpan stall, string families, string lastFailure)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"no scheduler step admitted a clock window within {WaitError.FormatDuration(stall)} of the watch starting (RIMGOVERNOR_ROUTINE_FAMILIES=\"{families}\")");
        if (lastFailure.Length > 0)
        {
            b.Append(": ").Append(lastFailure);
        }

        b.Append("; the composed step is starved of its budget -- on a shared machine narrow -families to the family under test (e.g. field) or run without peer games");
        return b.ToString();
    }
}

/// <summary>Step-stall helpers (Go <c>stepstall.go</c>).</summary>
public static class StepStall
{
    /// <summary>The service's most recent failed step from its OTLP log ring at <paramref name="path"/>, or "" when it has none or cannot be read.</summary>
    public static string LastStepFailure(string path)
    {
        var found = LogTail.LastLogRow(path, 0, r => RowKinds.IsWorkerStep(r) && r.ContextString("level") is "WARN" or "ERROR");
        return found is { } hit ? StepFailureText(hit.Row) : "";
    }

    /// <summary>
    /// Renders a failed worker-step row as "&lt;at&gt; tick=&lt;n&gt; &lt;level&gt; &lt;verdict&gt; &lt;reason&gt;: &lt;error&gt;"
    /// (the row's error attr and isolated planner failures carry the text).
    /// </summary>
    public static string StepFailureText(LogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var at = row.ContextString("at");
        var parts = new List<string>();
        if (at.Length > 0)
        {
            parts.Add(at);
        }

        if (row.HasTick)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"tick={row.Tick}"));
        }

        parts.Add(row.ContextString("level"));
        parts.Add(StepFailureWords(row));
        return string.Join(" ", parts);
    }

    // The worker-step row's "<verdict> <reason>" followed by its error and the isolated planner failures when it carries them.
    private static string StepFailureWords(LogRow row)
    {
        var fields = row.Fields();
        var words = new List<string>();
        foreach (var key in new[] { "verdict", "reason" })
        {
            if (RowValue.Text(fields.GetValueOrDefault(key)) is { Length: > 0 } word)
            {
                words.Add(word);
            }
        }

        var text = string.Join(" ", words);
        if (RowValue.Text(fields.GetValueOrDefault("error")) is { Length: > 0 } error)
        {
            text += ": " + error;
        }

        var failures = StepEvents.PlannerFailures(row);
        if (failures != "[]")
        {
            text += " " + failures;
        }

        return text;
    }
}
