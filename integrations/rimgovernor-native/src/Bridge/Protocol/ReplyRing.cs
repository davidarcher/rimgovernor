#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The reply ring: a caller that asked for encoding=proto-shm gets
    /// a reply of InlineBytes or more as raw proto in a slot of this named
    /// shared-memory ring, and the GABP reply carries only
    /// {"slot":{"ring","slot","seq","length"}}; the controller
    /// (go/internal/bridge/replyslots.go) copies the slot and checks the seq.
    ///
    /// The slot layout is the snapshot ring's (SnapshotStream), under magic
    /// "RGRR": header 64 bytes (magic, version, slots, slot bytes); entry n in
    /// slot n % Slots behind a seqlock (2n+1 while writing, 2n committed),
    /// payload length at +16, payload at +40. The ring is fixed and sized well
    /// above the encoder workers (ReplyEncoder.Slots), so an entry overwritten
    /// before the controller read it is its contract error, not a retry. A
    /// reply over a slot, or a ring that could not be created, stays inline.
    ///
    /// Names are per process: Windows Local\RimGovernorReplies-&lt;pid&gt;-&lt;guid&gt;;
    /// Linux /dev/shm/RimGovernorReplies-&lt;pid&gt;-&lt;guid&gt;, swept like the
    /// snapshot ring's when its process is gone.
    /// </summary>
    internal static class ReplyRing
    {
        internal const int Slots = 8;
        internal const int SlotBytes = 16 << 20;
        internal const int HeaderBytes = 64, SlotHeaderBytes = 40;
        /// Replies below this many proto bytes stay inline proto-gzip. Measured on
        /// Windows: the slot saves 15-20% of call_ms from 0.5 MB up
        /// and only a few ms near this size, so a lower threshold buys little
        /// and puts more replies through the 8-slot ring.
        internal const int InlineBytes = 256 << 10;
        private const uint Magic = 0x52524752; // "RGRR"
        private const uint Version = 1;
        private const string ShmDir = "/dev/shm", Prefix = "RimGovernorReplies-";

        private static readonly object Gate = new object();
        private static readonly object[] SlotLocks = { new object(), new object(), new object(), new object(), new object(), new object(), new object(), new object() };
        private static string? name;
        private static MemoryMappedFile? mapping;
        private static MemoryMappedViewAccessor? view;
        private static bool failed;
        private static long next;

        /// <summary>
        /// Writes payload into the next slot and returns the wrapper's slot
        /// value, or null when the reply must stay inline.
        /// </summary>
        internal static Dictionary<string, object?>? Write(byte[] payload)
        {
            if (payload.Length > SlotBytes - SlotHeaderBytes || !Ensure()) return null;
            var n = (ulong)Interlocked.Increment(ref next);
            var slot = (long)(n % Slots);
            lock (SlotLocks[slot])
            {
                long offset = HeaderBytes + slot * SlotBytes;
                var v = view!;
                v.Write(offset, 2 * n + 1);
                Thread.MemoryBarrier();
                v.Write(offset + 16, (uint)payload.Length);
                bool retained = false;
                var handle = v.SafeMemoryMappedViewHandle;
                try
                {
                    handle.DangerousAddRef(ref retained);
                    Marshal.Copy(payload, 0, IntPtr.Add(handle.DangerousGetHandle(), (int)(offset + SlotHeaderBytes)), payload.Length);
                }
                finally { if (retained) handle.DangerousRelease(); }
                Thread.MemoryBarrier();
                v.Write(offset, 2 * n);
                Thread.MemoryBarrier();
            }
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ring"] = name,
                ["slot"] = (long)slot,
                ["seq"] = (long)n,
                ["length"] = (long)payload.Length,
            };
        }

        private static bool Ensure()
        {
            if (Volatile.Read(ref view) != null) return true;
            lock (Gate)
            {
                if (view != null) return true;
                if (failed) return false;
                try { Create(); return true; }
                catch (Exception)
                {
                    // The caller's reply goes inline; so does every later one.
                    failed = true;
                    return false;
                }
            }
        }

        private static void Create()
        {
            long capacity = HeaderBytes + (long)Slots * SlotBytes;
            var unique = Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
            string ringName;
            MemoryMappedFile file;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                SweepDeadRings();
                ringName = ShmDir + "/" + Prefix + unique;
                var stream = new FileStream(ringName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                stream.SetLength(capacity);
                file = MemoryMappedFile.CreateFromFile(stream, null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                var own = ringName;
                AppDomain.CurrentDomain.ProcessExit += (_, __) => { try { File.Delete(own); } catch (Exception) { } };
            }
            else
            {
                ringName = "Local\\" + Prefix + unique;
                file = MemoryMappedFile.CreateNew(ringName, capacity);
            }
            var accessor = file.CreateViewAccessor();
            accessor.Write(0, Magic);
            accessor.Write(4, Version);
            accessor.Write(8, (uint)Slots);
            accessor.Write(12, (uint)SlotBytes);
            Thread.MemoryBarrier();
            name = ringName;
            mapping = file;
            Volatile.Write(ref view, accessor);
        }

        // Deletes the ring files of processes that are gone.
        private static void SweepDeadRings()
        {
            try
            {
                foreach (var path in Directory.GetFiles(ShmDir, Prefix + "*"))
                {
                    var rest = Path.GetFileName(path).Substring(Prefix.Length);
                    var dash = rest.IndexOf('-');
                    if (dash <= 0 || !int.TryParse(rest.Substring(0, dash), out var pid)) continue;
                    if (Directory.Exists("/proc/" + pid)) continue;
                    try { File.Delete(path); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }
}
