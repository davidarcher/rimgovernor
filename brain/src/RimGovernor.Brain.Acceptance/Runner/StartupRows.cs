namespace RimGovernor.Brain.Acceptance;

/// <summary>The closing check every run ends on (Go <c>cases/startuplog.go</c>).</summary>
public static class StartupRows
{
    /// <summary>
    /// The mod's startup rows (ModLog, kind mod_log, source "startup") in &lt;output&gt;/otlp/logs.jsonl and its rotated
    /// segments carry no error, and a windowed run has no "headless mode active" row. The row is not required of a
    /// headless run: a long run rotates the ring and the startup row can be trimmed away. A run that never started a
    /// service has no ring and no startup guard.
    /// </summary>
    public static void Check(string output, bool headless)
    {
        IReadOnlyList<LogRow> rows;
        try
        {
            rows = ServiceLogs.ReadLogs(ServiceLogs.ServiceLogsPath(output));
        }
        catch (IOException e)
        {
            throw new AcceptanceException("read log ring: " + e.Message, e);
        }

        foreach (var row in rows)
        {
            if (row.Kind != "mod_log" || RowText(row, "source") != "startup")
            {
                continue;
            }

            var message = RowText(row, "msg");
            if (RowText(row, "level") == "error")
            {
                throw new AcceptanceException("startup error logged by the mod: " + message);
            }

            if (message == "headless mode active" && !headless)
            {
                throw new AcceptanceException("headless initialization disagrees with launch mode (headless=false, \"headless mode active\" row present)");
            }
        }
    }

    private static string RowText(LogRow row, string key) => row.Payload.GetValueOrDefault(key) as string ?? "";
}
