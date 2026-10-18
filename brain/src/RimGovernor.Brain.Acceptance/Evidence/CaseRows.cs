using Microsoft.Data.Sqlite;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The store file a case's brain wrote and the case's window in it (#3437), which the evidence dump and the failure
/// digest both read. A kept game keeps the state path it was launched with, an earlier case's <c>service.sqlite</c>,
/// so the file is the live path (<see cref="ServiceJournal.PathOf"/>) and holds the earlier cases' rows too. With a
/// <see cref="Since"/>, a row is the case's when:
/// <list type="bullet">
/// <item><c>need_transitions</c>: its <c>wall_ms</c> is at or after the case start;</item>
/// <item><c>need_methods</c>: its <c>load_token</c> is the case's load. No table ties a load token to wall time, so the
/// case's load is the one the persisted review (<c>rounds</c>) names: a world rebuild, which every load makes, clears
/// the review, so a review standing at case end is of the case's last load, provided the brain filed in the window (a
/// <c>need_transitions</c> row since the case start; else the review may be an earlier case's);</item>
/// <item><c>transitions</c> (plan stages): its sequence is at or after the first one whose snapshot names that load
/// (receipts carry no snapshot; the sequence only grows).</item>
/// </list>
/// A case that reloads keeps only its last load's methods and stages; one with no review or no filing in the window
/// has none. Without a <see cref="Since"/> every row counts (<c>acceptance why</c> on an output's own store).
/// </summary>
public sealed record CaseRows(string Path, DateTime? Since)
{
    private const string SinceParameter = "$since";

    private const string CaseLoad =
        "SELECT json_extract(payload,'$.Snapshot.Load') FROM rounds WHERE singleton=1 AND EXISTS(SELECT 1 FROM need_transitions WHERE wall_ms>=$since)";

    /// <summary>The live store of the case <paramref name="config"/> runs, windowed from <paramref name="started"/>.</summary>
    public static CaseRows Of(Config config, DateTime started) => new(ServiceJournal.PathOf(config), started);

    /// <summary>Every row of the store file at <paramref name="path"/>.</summary>
    public static CaseRows Whole(string path) => new(path, null);

    /// <summary>The <c>need_transitions</c> rows of the case, as an SQL predicate.</summary>
    internal string TransitionsPredicate => Since is null ? "1" : "wall_ms>=" + SinceParameter;

    /// <summary>The <c>need_methods</c> rows of the case, as an SQL predicate on its unqualified <c>load_token</c>.</summary>
    internal string MethodsPredicate => Since is null ? "1" : $"load_token=({CaseLoad})";

    /// <summary>The <c>transitions</c> rows of the case, as an SQL predicate on its unqualified <c>sequence</c>.</summary>
    internal string StagesPredicate =>
        Since is null ? "1" : $"sequence>=(SELECT min(sequence) FROM transitions WHERE json_extract(payload,'$.Snapshot.Load')=({CaseLoad}))";

    /// <summary>Binds the window's start, in unix milliseconds, for the predicates.</summary>
    internal void Bind(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Since is { } since)
        {
            command.Parameters.AddWithValue(SinceParameter, new DateTimeOffset(since.ToUniversalTime()).ToUnixTimeMilliseconds());
        }
    }
}
