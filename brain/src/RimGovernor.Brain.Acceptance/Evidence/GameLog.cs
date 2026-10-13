using System.Text;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Retains the slice of the game's log (the instance's captured stdout, shared by every case on a kept process and
/// overwritten by a relaunch) that a case wrote: <see cref="Open"/> notes where the log ends when the case opens,
/// and <see cref="Close"/> copies from there to <c>&lt;output&gt;/game.log</c>. A native exception mid-case is then
/// in the case's own evidence instead of a root-level file the next case overwrites.
/// </summary>
public sealed class GameLogCapture
{
    /// <summary>The per-case copy of the game's own log under the case's output directory.</summary>
    public const string GameLogName = "game.log";

    /// <summary>The result.json field <see cref="Close"/> records.</summary>
    public const string GameLogKey = "game_log";

    private GameLogCapture(string source, long offset)
    {
        Source = source;
        Offset = offset;
    }

    public string Source { get; }

    public long Offset { get; }

    /// <summary>Starts a capture at the current end of <paramref name="source"/> (zero when the log does not exist yet: a launch is coming).</summary>
    public static GameLogCapture Open(string source) => new(source, File.Exists(source) ? new FileInfo(source).Length : 0);

    /// <summary>
    /// Copies the log from the capture's offset (from the start when the log is shorter than it was at open: the
    /// process relaunched) into <c>&lt;output&gt;/game.log</c> and records on <paramref name="report"/> under game_log
    /// the copy's path, bytes and exceptions, the count of lines naming an Exception. The count is recorded, not
    /// judged. A missing or unreadable source records nothing.
    /// </summary>
    public void Close(string output, Report? report)
    {
        var path = Path.Combine(output, GameLogName);
        long written;
        int exceptions;
        try
        {
            using var source = new FileStream(Source, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
            });
            var offset = source.Length < Offset ? 0 : Offset;
            source.Seek(offset, SeekOrigin.Begin);
            using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                source.CopyTo(target);
                written = target.Length;
            }

            exceptions = CountExceptionLines(path);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if (report is not null)
        {
            report[GameLogKey] = new JsonObject { ["path"] = path, ["bytes"] = written, ["exceptions"] = exceptions };
        }
    }

    // The lines of the file that name an Exception (NullReferenceException, InvalidOperationException, ...).
    private static int CountExceptionLines(string path)
    {
        var count = 0;
        using var reader = new StreamReader(path, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("Exception", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
