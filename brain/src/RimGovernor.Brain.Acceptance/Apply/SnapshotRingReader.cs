using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace RimGovernor.Brain.Acceptance;

/// <summary>One committed frame copied out of a snapshot ring (Go <c>snapshotshm.Frame</c>).</summary>
internal sealed record SnapshotRingFrame(ulong Number, long Writes, byte[] Payload, uint CaptureMicros, uint EncodeMicros, uint WriteMicros);

/// <summary>
/// Reads the snapshot stream (Go <c>snapshotshm</c>): the ring of BundleSnapshot frames the native SnapshotStream
/// publishes into named shared memory. Reads never lock: each slot is a seqlock, so a reader copies a frame and keeps it
/// only when the slot's sequence did not move while it copied. It only works when the harness shares a host with the
/// game. Open throws <see cref="AcceptanceException"/> otherwise.
/// </summary>
/// <remarks>
/// Layout (little-endian): a 64-byte header (uint32 magic "RGSS", uint32 version, uint32 slots, uint32 slot bytes,
/// uint64 head: the last committed frame number, 0 for none; int64 writes: the native applied-write counter), then slot
/// i at 64 + i * slotBytes; frame n lives in slot n % slots: uint64 seqlock (2n+1 while writing, 2n once committed),
/// int64 writes the frame was captured at, uint32 payload length, uint32 capture, encode and write microseconds, then the
/// payload at slot offset 40. Go also waited on the ring's named ready event to avoid polling; the C# reader polls
/// every 2 ms, which the frame cadence (one capture per write or per wall second) makes indistinguishable.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SnapshotRingReader : IDisposable
{
    private const uint Magic = 0x53534752;
    private const uint Version = 2;
    private const int HeaderBytes = 64;
    private const int SlotHeaderBytes = 40;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly ulong _slots;
    private readonly ulong _slotBytes;
    private unsafe byte* _base;

    private unsafe SnapshotRingReader(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* basePointer, ulong slots, ulong slotBytes)
    {
        _file = file;
        _view = view;
        _base = basePointer;
        _slots = slots;
        _slotBytes = slotBytes;
    }

    /// <summary>Maps the named snapshot ring (<c>Local\RimGovernorSnapshot-&lt;id&gt;</c>) read-only.</summary>
    public static unsafe SnapshotRingReader Open(string name)
    {
        MemoryMappedFile file;
        try
        {
            file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            throw new AcceptanceException($"snapshot shared memory unavailable: {e.Message}", e);
        }

        MemoryMappedViewAccessor? view = null;
        try
        {
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointer += view.PointerOffset;
            var capacity = (ulong)view.Capacity;
            if (capacity < HeaderBytes)
            {
                throw new AcceptanceException("snapshot shared memory unavailable: ring below its header");
            }

            var header = new ReadOnlySpan<byte>(pointer, HeaderBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[0..4]) != Magic || BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) != Version)
            {
                throw new AcceptanceException("snapshot shared memory unavailable: ring magic or version differs");
            }

            var slots = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
            var slotBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
            if (slots < 2 || slotBytes <= SlotHeaderBytes || capacity < HeaderBytes + ((ulong)slots * slotBytes))
            {
                throw new AcceptanceException($"snapshot shared memory unavailable: ring geometry {slots}x{slotBytes} does not fit {capacity} bytes");
            }

            var reader = new SnapshotRingReader(file, view, pointer, slots, slotBytes);
            view = null;
            return reader;
        }
        catch
        {
            if (view is not null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
            }

            file.Dispose();
            throw;
        }
    }

    /// <summary>The last committed frame number, 0 before the first.</summary>
    public ulong Head() => Load(16);

    /// <summary>
    /// The native applied-write counter now: a frame captured at or past it reflects every write that returned before
    /// this call.
    /// </summary>
    public long Writes() => (long)Load(24);

    /// <summary>Copies the newest committed frame; null before the first. A frame overwritten while it was copied is retried from the new head.</summary>
    public SnapshotRingFrame? Latest()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var n = Head();
            if (n == 0)
            {
                return null;
            }

            if (Read(n) is { } frame)
            {
                return frame;
            }
        }

        return null;
    }

    /// <summary>Copies frame <paramref name="n"/>, or null when its slot no longer holds it (overwritten, or being written).</summary>
    public unsafe SnapshotRingFrame? Read(ulong n)
    {
        var slot = HeaderBytes + ((n % _slots) * _slotBytes);
        var before = Load(slot);
        if (before != 2 * n)
        {
            return null;
        }

        var slotSpan = new ReadOnlySpan<byte>(_base + slot, (int)_slotBytes);
        var writes = BinaryPrimitives.ReadInt64LittleEndian(slotSpan[8..16]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(slotSpan[16..20]);
        if (length > _slotBytes - SlotHeaderBytes)
        {
            // A torn header: the recheck decides.
            return Load(slot) != before
                ? null
                : throw new AcceptanceException($"snapshot frame {n} claims {length} bytes, over its {_slotBytes - SlotHeaderBytes}-byte slot");
        }

        var capture = BinaryPrimitives.ReadUInt32LittleEndian(slotSpan[20..24]);
        var encode = BinaryPrimitives.ReadUInt32LittleEndian(slotSpan[24..28]);
        var write = BinaryPrimitives.ReadUInt32LittleEndian(slotSpan[28..32]);
        var payload = slotSpan.Slice(SlotHeaderBytes, (int)length).ToArray();
        return Load(slot) != before ? null : new SnapshotRingFrame(n, writes, payload, capture, encode, write);
    }

    /// <summary>Waits until a frame past <paramref name="after"/> is committed, the token ends or <paramref name="timeout"/> passes; reports whether one was.</summary>
    public async Task<bool> WaitAsync(ulong after, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (Head() > after)
            {
                return true;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(left < PollInterval ? left : PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    /// <summary>Unmaps the ring.</summary>
    public unsafe void Dispose()
    {
        if (_base is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
            _view.Dispose();
            _file.Dispose();
        }
    }

    // Reads an aligned uint64 the producer writes, atomically.
    private unsafe ulong Load(ulong offset) => Volatile.Read(ref *(ulong*)(_base + offset));
}
