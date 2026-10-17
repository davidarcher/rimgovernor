using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RimGovernor.Tools.Common;

/// <summary>
/// A cross-process lock held by keeping a file open exclusively. The operating system closes the handle when the
/// holder exits, however it exits, so a killed holder never leaves the lock taken; the file itself stays and means
/// nothing. Who holds it is written to a sidecar <c>&lt;path&gt;.holder</c> for waiters to report.
/// </summary>
public sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _holderPath;

    private FileLock(FileStream stream, string holderPath)
    {
        _stream = stream;
        _holderPath = holderPath;
    }

    /// <summary>Takes the lock now, or returns null when another process holds it.</summary>
    public static FileLock? TryAcquire(string path, string holder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        FileStream stream;
        try
        {
            // FileShare.None is a sharing-mode deny on Windows and an exclusive flock on Unix.
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        var holderPath = path + ".holder";
        try
        {
            File.WriteAllText(
                holderPath,
                string.Create(CultureInfo.InvariantCulture, $"pid={Environment.ProcessId} {holder} since={DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}\n"),
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // The sidecar only names the holder for waiters.
        }
        return new FileLock(stream, holderPath);
    }

    /// <summary>
    /// Takes the lock, waiting up to <paramref name="timeout"/> for a holder to finish and reporting each wait through
    /// <paramref name="waiting"/>.
    /// </summary>
    public static async Task<FileLock> AcquireAsync(
        string path,
        string holder,
        TimeSpan timeout,
        TimeSpan poll,
        Action<string> waiting,
        CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (TryAcquire(path, holder) is { } taken)
            {
                return taken;
            }
            var current = Holder(path);
            if (elapsed.Elapsed > timeout)
            {
                throw new ToolException($"lock {path} held for over {Durations.Format(timeout)} by {current}");
            }
            waiting(current);
            await Task.Delay(poll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The holder a lock's sidecar names, or a placeholder when it cannot be read.</summary>
    public static string Holder(string path)
    {
        try
        {
            using var stream = new FileStream(path + ".holder", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            return text.Length == 0 ? "another process" : text;
        }
        catch (IOException)
        {
            return "another process";
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_holderPath);
        }
        catch (IOException)
        {
            // A stale sidecar is only read while the lock is held by someone else, who rewrote it.
        }
        _stream.Dispose();
    }
}
