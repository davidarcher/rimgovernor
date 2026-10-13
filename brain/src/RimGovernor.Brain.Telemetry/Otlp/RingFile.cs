using System.Globalization;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>
/// Appends lines to <c>path</c>. Once the active file would pass <c>segmentBytes</c> it is renamed to
/// <c>path.1</c> (older ones shift to <c>path.2</c> ... and the oldest beyond <c>segments</c> files in total is
/// deleted), so disk use is bounded by segmentBytes x segments. Writes go straight to the OS (no user-space
/// buffer): a process crash loses nothing that <see cref="Write"/> returned for. fsync happens only on rotation,
/// close and when the caller asks (durable). A batch is never split across segments.
/// </summary>
internal sealed class RingFile
{
    private readonly string _path;
    private readonly long _segmentBytes;
    private readonly int _segments;
    private readonly object _gate = new();
    private FileStream? _file;
    private long _size;
    private bool _closed;

    internal RingFile(string path, long segmentBytes, int segments)
    {
        if (segmentBytes < 1 || segments < 2)
        {
            throw new OtlpException("file bounds are too small");
        }
        _path = path;
        _segmentBytes = segmentBytes;
        _segments = segments;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            Open();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OtlpException(e.Message, e);
        }
    }

    // Opens the active file for append. After an unclean exit the file can end in a partial line: a newline is
    // written first so the next record starts on its own line and only the torn one is unreadable. Shared with
    // readers and renamers, which is how a reader follows a live ring on Windows.
    private void Open()
    {
        var file = new FileStream(_path, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0,
        });
        try
        {
            var size = file.Length;
            if (size > 0)
            {
                file.Seek(size - 1, SeekOrigin.Begin);
                if (file.ReadByte() != '\n')
                {
                    file.Seek(0, SeekOrigin.End);
                    file.WriteByte((byte)'\n');
                    size++;
                }
            }
            file.Seek(0, SeekOrigin.End);
            _file = file;
            _size = size;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private string SegmentPath(int n) => _path + "." + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Appends <paramref name="lines"/> (whole lines); a durable write is fsync'd.</summary>
    internal void Write(ReadOnlySpan<byte> lines, bool durable)
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new OtlpException("file sink closed");
            }
            try
            {
                if (_file is null)
                {
                    Open();
                }
                if (_size > 0 && _size + lines.Length > _segmentBytes)
                {
                    Rotate();
                }
                _file!.Write(lines);
                _size += lines.Length;
                if (durable)
                {
                    _file.Flush(flushToDisk: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new OtlpException(e.Message, e);
            }
        }
    }

    private void Rotate()
    {
        _file!.Flush(flushToDisk: true);
        _file.Dispose();
        _file = null;
        File.Delete(SegmentPath(_segments - 1));
        for (var n = _segments - 2; n >= 1; n--)
        {
            if (File.Exists(SegmentPath(n)))
            {
                File.Move(SegmentPath(n), SegmentPath(n + 1), overwrite: true);
            }
        }
        File.Move(_path, SegmentPath(1), overwrite: true);
        Open();
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            if (_file is null)
            {
                return;
            }
            try
            {
                _file.Flush(flushToDisk: true);
            }
            finally
            {
                _file.Dispose();
                _file = null;
            }
        }
    }
}
