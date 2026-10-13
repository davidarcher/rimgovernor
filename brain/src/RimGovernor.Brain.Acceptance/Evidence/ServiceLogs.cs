using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A service's evidence is its OTLP/JSON file sink (docs/developers/contracts/otel-core.md): traces.jsonl, logs.jsonl
/// and metrics.jsonl under an otlp directory. Decisions, events and the native rows are the log records; the case
/// oracles read them as <see cref="LogRow"/>s (Go <c>logs.go</c>).
/// </summary>
public static class ServiceLogs
{
    /// <summary>The OTLP directory of a service whose evidence lives under <paramref name="dir"/>.</summary>
    public static string OtlpDir(string dir) => Path.Combine(dir, "otlp");

    /// <summary>The log ring of the service whose OTLP directory is <paramref name="otlpDir"/>: otlpDir/logs.jsonl.</summary>
    public static string LogsPath(string otlpDir) => Path.Combine(otlpDir, OtlpFiles.Logs);

    /// <summary>The log ring of the first service launch under <paramref name="output"/>; the runner's metrics block summarizes it.</summary>
    public static string ServiceLogsPath(string output) => LogsPath(OtlpDir(output));

    /// <summary>
    /// Reads every row of a service's log ring at <paramref name="path"/> from its start, across the ring's rotated
    /// segments (<see cref="LogTail"/> follows appends only): what a case audits after the service stopped. A missing
    /// ring is an empty recording.
    /// </summary>
    public static IReadOnlyList<LogRow> ReadLogs(string path)
    {
        var records = OtlpTimeline.Read(path);
        var rows = new List<LogRow>(records.Count);
        foreach (var record in records)
        {
            rows.Add(LogRow.From(record, (ulong)record.Sequence));
        }

        return rows;
    }

    /// <summary>The msg payloads of every row of <paramref name="kind"/> in the log ring at <paramref name="path"/>, oldest first: the building planner's placement_refused and foreign_held rows carry what they report there.</summary>
    public static IReadOnlyList<string> LogMessages(string path, string kind)
    {
        var messages = new List<string>();
        foreach (var row in ReadLogs(path))
        {
            if (row.Kind == kind && row.Payload.GetValueOrDefault("msg") is string msg)
            {
                messages.Add(msg);
            }
        }

        return messages;
    }

    /// <summary>
    /// Returns every placement_refused message in the log ring at <paramref name="path"/> and throws when one names no
    /// blocker: a refused furniture cell reports the thing the native preview found in the way.
    /// </summary>
    public static IReadOnlyList<string> RefusalsNameBlockers(string path)
    {
        var refused = LogMessages(path, "placement_refused");
        const string Separator = " blocked by ";
        foreach (var msg in refused)
        {
            var at = msg.IndexOf(Separator, StringComparison.Ordinal);
            var blocker = at < 0 ? "" : msg[(at + Separator.Length)..];
            if (blocker.Length == 0 || blocker == "unreported")
            {
                throw new AcceptanceException($"a refused cell reports no blocker: \"{msg}\"");
            }
        }

        return refused;
    }
}
