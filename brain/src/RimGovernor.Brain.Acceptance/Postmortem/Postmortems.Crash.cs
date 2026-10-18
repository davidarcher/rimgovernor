using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

public static class PostmortemCrash
{
    /// <summary>The game.log lines a startup crash shows: the fatal error and the frames above it.</summary>
    internal const int LogTailLines = 8;

    /// <summary>
    /// Names the runtime's crash dumps in the case directory (<see cref="CrashDump.FilePattern"/>) and, when the case
    /// failed on the startup crash (<see cref="CrashDump.IsStartupCrash"/>), the tail of the case's game.log: the
    /// evidence #3190 needs from each recurrence. A startup crash that left no dump says so, since that means the
    /// runtime died before createdump could run.
    /// </summary>
    internal static DigestSection CrashDumps(string dir, string error)
    {
        var s = new DigestSection("crash dump");
        var dumps = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, CrashDump.FilePattern).Order(StringComparer.Ordinal).ToList()
            : [];
        foreach (var dump in dumps)
        {
            var name = Path.GetFileName(dump);
            s.Lines.Add(new DigestLine(string.Create(CultureInfo.InvariantCulture, $"CoreCLR crash dump {name} ({new FileInfo(dump).Length} bytes), kept beside the case evidence (process memory: never exported)"), name));
        }

        if (!CrashDump.IsStartupCrash(error))
        {
            if (dumps.Count == 0)
            {
                s.Note = "no " + CrashDump.FilePattern + " in the case directory";
            }

            return s;
        }

        s.Lines.Add(new DigestLine(string.Create(CultureInfo.InvariantCulture, $"the game exited with 0xC0000005 ({CrashDump.AccessViolationExit}) before its channel came up"), "result.json error"));
        if (dumps.Count == 0)
        {
            s.Note = "startup crash without a dump: the runtime died before createdump ran, or the launch lacked " + CrashDump.EnableVariable;
        }

        var log = Path.Combine(dir, GameLogCapture.GameLogName);
        var tail = new Queue<(int Line, string Text)>();
        try
        {
            var n = 0;
            foreach (var line in File.ReadLines(log))
            {
                n++;
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                tail.Enqueue((n, line.Trim()));
                if (tail.Count > LogTailLines)
                {
                    _ = tail.Dequeue();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            s.Lines.Add(new DigestLine("no readable game.log: " + e.Message, GameLogCapture.GameLogName));
        }

        foreach (var (line, text) in tail)
        {
            s.Lines.Add(new DigestLine(text, string.Create(CultureInfo.InvariantCulture, $"{GameLogCapture.GameLogName}#{line}")));
        }

        return s;
    }
}
