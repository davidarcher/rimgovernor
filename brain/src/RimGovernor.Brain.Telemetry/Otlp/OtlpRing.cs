using System.Globalization;
using System.Text.Json;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>
/// Locates one line of a ring: the segment file it was read from (<c>traces.jsonl</c>, <c>logs.jsonl.2</c>, ...) and
/// its 1-based line number there. A reader quotes it as <c>file#line</c>; the line number is what <c>grep -n</c> prints.
/// </summary>
public readonly record struct OtlpPosition(string File, int Line)
{
    public override string ToString() => Path.GetFileName(File) + "#" + Line.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Walks a file ring written by <see cref="OtlpFileWriter"/> (or by the Go service, which wrote the same format).
/// Every reader tolerates a live ring: a torn or unparsable line is skipped, and a rotation racing the read can
/// skip or repeat a segment.
/// </summary>
public static class OtlpRing
{
    /// <summary>The existing files of one signal's ring, oldest first: <c>path.N ... path.1</c>, then <c>path</c> itself.</summary>
    public static IReadOnlyList<string> Segments(string path)
    {
        var rotated = new List<string>();
        for (var n = 1; ; n++)
        {
            var segment = path + "." + n.ToString(CultureInfo.InvariantCulture);
            if (!File.Exists(segment))
            {
                break;
            }
            rotated.Add(segment);
        }
        rotated.Reverse();
        if (File.Exists(path))
        {
            rotated.Add(path);
        }
        return rotated;
    }

    /// <summary>
    /// Calls <paramref name="onLine"/> with every complete, valid-JSON line of the ring at <paramref name="path"/>, oldest
    /// first, with its position. The line is only valid during the call. Torn or invalid lines are skipped but still
    /// counted in the line numbers. A missing ring is empty.
    /// </summary>
    public static void Scan(string path, Action<OtlpPosition, ReadOnlyMemory<byte>> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        foreach (var segment in Segments(path))
        {
            FileStream file;
            try
            {
                file = new FileStream(segment, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = 0,
                });
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            using (file)
            {
                ScanSegment(file, segment, onLine);
            }
        }
    }

    /// <summary>Calls <paramref name="onLine"/> with each complete, valid-JSON line of the ring, oldest first.</summary>
    public static void ReadLines(string path, Action<ReadOnlyMemory<byte>> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        Scan(path, (_, line) => onLine(line));
    }

    private static void ScanSegment(Stream stream, string file, Action<OtlpPosition, ReadOnlyMemory<byte>> onLine)
    {
        var buffer = new byte[1 << 20];
        var pending = new MemoryStream();
        var number = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            while (start < read)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', start, read - start);
                if (newline < 0)
                {
                    pending.Write(buffer, start, read - start);
                    break;
                }
                number++;
                ReadOnlyMemory<byte> line;
                if (pending.Length > 0)
                {
                    pending.Write(buffer, start, newline - start);
                    line = pending.ToArray();
                    pending.SetLength(0);
                }
                else
                {
                    line = buffer.AsMemory(start, newline - start);
                }
                Deliver(new OtlpPosition(file, number), line, onLine);
                start = newline + 1;
            }
        }
        // A final partial line (no newline) is torn: it is counted nowhere and skipped.
    }

    private static void Deliver(OtlpPosition position, ReadOnlyMemory<byte> line, Action<OtlpPosition, ReadOnlyMemory<byte>> onLine)
    {
        var span = line.Span;
        if (span.Length > 0 && span[^1] == '\r')
        {
            line = line[..^1];
        }
        if (line.Length > 0 && IsValidJson(line.Span))
        {
            onLine(position, line);
        }
    }

    private static bool IsValidJson(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        try
        {
            while (reader.Read())
            {
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
