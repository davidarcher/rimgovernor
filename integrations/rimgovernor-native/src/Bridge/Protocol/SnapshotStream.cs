#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using UnityEngine;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The snapshot stream (#858): whole BundleSnapshot frames published into
    /// a named shared-memory ring, read lock-free by the controller
    /// (go/internal/snapshotshm). The game thread captures a frame right after
    /// a frame's ticks (Supervisor.OnFrame) every PeriodTicks, on a pause
    /// edge and after every applied write (a deferred
    /// write waits for observations_flush_snapshot or DeferredSeconds); an encoder worker formats it and
    /// writes it into the next slot under a per-slot seqlock, so the game
    /// thread never waits on a reader. Nothing is captured until the
    /// controller opens the stream.
    ///
    /// Layout (little-endian):
    ///   header, 64 bytes:
    ///     0  uint32 magic "RGSS"   4  uint32 version
    ///     8  uint32 slots          12 uint32 slot bytes
    ///     16 uint64 head: the last committed frame's number (0: none)
    ///     24 uint64 writes: applied-write counter, bumped on the game thread
    ///   slot i at 64 + i*slotBytes, frame n in slot n % slots:
    ///     0  uint64 seqlock: 2n+1 while writing, 2n once committed
    ///     8  uint64 writes the frame was captured at
    ///     16 uint32 payload length
    ///     20 uint32 capture microseconds (game thread)
    ///     24 uint32 encode microseconds  28 uint32 write microseconds
    ///     32 reserved
    ///     40 payload: BundleSnapshot binary
    /// The ready event (Windows only; a Linux reader polls) is name + "-ready",
    /// a manual-reset event set after each commit.
    ///
    /// Names are per process: Windows Local\RimGovernorSnapshot-&lt;pid&gt;-&lt;guid&gt;
    /// (kernel objects, freed with the last handle); Linux
    /// /dev/shm/RimGovernorSnapshot-&lt;pid&gt;-&lt;guid&gt;, a file that outlives a
    /// killed process, so each new ring deletes the files of dead pids and
    /// the ring unlinks its own on quit.
    /// </summary>
    public sealed class SnapshotStreamTools
    {
        private const string ToolName = "rimgovernor/observations_open_snapshot_stream";

        [Tool(ToolName, Title = "Open snapshot stream",
            Description = "Official SnapshotStreamRequest ProtoJSON. Opens (or resubscribes) the shared-memory snapshot ring and names it. Read-only.")]
        [ToolResponse("payload", "string", "Official observations SnapshotStreamReply ProtoJSON.", Always = true)]
        public async Task<object> Open(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a SnapshotStreamRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.SnapshotStreamRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.SnapshotStreamReply { Failure = failure });
            foreach (var resource in parsed.ResourceSources)
                if (!ProtoBoundary.IsIdentifier(resource))
                    return ProtoBoundary.Encode(new Obs.SnapshotStreamReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Resource sources must be identifiers.") });
            if (!parsed.Definitions.All(ProtoBoundary.IsIdentifier) || parsed.Definitions.Distinct().Count() != parsed.Definitions.Count)
                return ProtoBoundary.Encode(new Obs.SnapshotStreamReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Definitions must be distinct identifiers.") });
            return await ProtoBoundary.OnMainThread(ctx, () => ProtoBoundary.Encode(SnapshotStream.Open(parsed)), cancellationToken).ConfigureAwait(false);
        }

        private const string FlushToolName = "rimgovernor/observations_flush_snapshot";

        [Tool(FlushToolName, Title = "Flush snapshot",
            Description = "Official FlushSnapshotRequest ProtoJSON. Makes the next frame capture if an applied write is uncaptured (deferred writes, #1274). Returns without waiting for the capture.")]
        [ToolResponse("payload", "string", "Official observations FlushSnapshotReply ProtoJSON.", Always = true)]
        public Task<object> Flush(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a FlushSnapshotRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, FlushToolName, request!, Obs.FlushSnapshotRequest.Parser, out _, out var failure))
                return Task.FromResult<object>(ProtoBoundary.Encode(new Obs.FlushSnapshotReply { Failure = failure }));
            return Task.FromResult<object>(ProtoBoundary.Encode(new Obs.FlushSnapshotReply { Flushed = SnapshotStream.Flush() }));
        }
    }

    internal static class SnapshotStream
    {
        internal const int Slots = 3;
        internal const int SlotBytes = 32 << 20;
        internal const int HeaderBytes = 64, SlotHeaderBytes = 40;
        internal const int PeriodTicks = 60;
        internal const long PausedPeriodSeconds = 1;
        private const uint Magic = 0x53534752; // "RGSS"
        private const uint Version = 2;

        private static readonly object Gate = new object();
        private static Ring? ring;
        private static Obs.SnapshotStreamRequest subscription = new Obs.SnapshotStreamRequest();

        /// The shape the stream captures now (the default request until a
        /// controller subscribes); test/profile_capture profiles it (#1320).
        internal static Obs.SnapshotStreamRequest Subscription { get { lock (Gate) return subscription.Clone(); } }
        // Game-thread state: what the last capture saw.
        private static long writes, capturedWrites = -1;
        // due: the write count the next frame must reflect. A deferred
        // write (#1274) leaves it behind writes until a flush, a later
        // undeferred write, or DeferredSeconds after the oldest uncaptured
        // deferred write (deferredAt, game thread; 0: none).
        private static long due;
        private static long deferredAt;
        internal const long DeferredSeconds = 1;
        private static int capturedTick = int.MinValue;
        private static bool capturedPaused;
        private static int pending; // captures handed to the encoder, not yet written

        /// On the game thread: an applied write the next frame must reflect,
        /// or, deferred, one a flush or the safety net makes it capture.
        internal static void NoteWrite(bool deferred = false)
        {
            var r = ring;
            var value = Interlocked.Increment(ref writes);
            r?.WriteHeader(24, value);
            if (!deferred) { RaiseDue(value); return; }
            if (deferredAt == 0) deferredAt = Stopwatch.GetTimestamp();
        }

        /// Any thread: the next frame captures every write applied so far.
        /// Reports whether a write was uncaptured.
        internal static bool Flush()
        {
            var w = Interlocked.Read(ref writes);
            RaiseDue(w);
            return w != Interlocked.Read(ref capturedWrites);
        }

        private static void RaiseDue(long value)
        {
            long seen;
            while ((seen = Interlocked.Read(ref due)) < value)
                if (Interlocked.CompareExchange(ref due, value, seen) == seen) return;
        }

        internal static Obs.SnapshotStreamReply Open(Obs.SnapshotStreamRequest request)
        {
            lock (Gate)
            {
                // Frames are captured from the supervised-play frame hook; a
                // stream opened before any clock start (a lab case, #876)
                // installs it itself.
                try { Supervisor.EnsurePatched(); ring ??= new Ring(); }
                catch (Exception e)
                {
                    return new Obs.SnapshotStreamReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "Snapshot ring could not be created: " + e.Message } };
                }
                subscription = request.Clone();
                capturedWrites = -1; // the new subscription publishes at once
                return new Obs.SnapshotStreamReply { Opened = new Obs.SnapshotStreamOpened { Name = ring.Name, Slots = Slots, SlotBytes = SlotBytes } };
            }
        }

        /// The TickManagerUpdate postfix, on the game thread, once per frame.
        internal static void OnFrame()
        {
            var r = ring;
            if (r == null) return;
            var tm = Find.TickManager;
            var map = Find.CurrentMap;
            if (tm == null || map == null) return;
            var tick = tm.TicksGame;
            var paused = tm.Paused;
            var w = Interlocked.Read(ref writes);
            // Deferred writes wait for a flush, or DeferredSeconds at most.
            var needed = Interlocked.Read(ref due);
            if (deferredAt != 0 && Stopwatch.GetTimestamp() - deferredAt >= DeferredSeconds * Stopwatch.Frequency) needed = w;
            var period = CombatMirror.Dirty ? CombatMirror.MinCombatCompareTicks : PeriodTicks;
            var periodic = needed <= capturedWrites && paused == capturedPaused && tick >= capturedTick;
            // A paused game advances no tick, so the period is wall time
            // there (#838): a change no native write made (a fixture op, a
            // player edit while paused) is otherwise never captured.
            if (periodic && (paused ? Stopwatch.GetTimestamp() - capturedAt < PausedPeriodSeconds * Stopwatch.Frequency : tick - capturedTick < period)) return;
            // A periodic frame waits out DutyCycle times the last capture's
            // cost, so the stream holds the game thread at most ~1/DutyCycle
            // of wall time (#858: a capture is ~200 ms, a period at
            // Ultrafast ~150 ms). Writes and pause edges capture at once.
            if (periodic && Stopwatch.GetTimestamp() - capturedAt < (long)lastCaptureMicros * DutyCycle * Stopwatch.Frequency / 1_000_000) return;
            // One frame in flight at a time: a slow encode delays the next
            // capture instead of queueing stale ones.
            if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
            Obs.BundleSnapshot? frame;
            Obs.SnapshotStreamRequest shape;
            lock (Gate) shape = subscription;
            var captureStarted = Stopwatch.GetTimestamp();
            var account = ObservationWork.BeginCapture();
            try { frame = SnapshotFrames.Capture(map, shape); }
            catch (Exception e)
            {
                Interlocked.Exchange(ref pending, 0);
                Log.WarningOnce("[RimGovernor] snapshot frame capture failed: " + e.Message, 0x5e858);
                return;
            }
            finally { ObservationWork.End(); }
            var captureMicros = Micros(captureStarted);
            NoteSlowCapture(captureMicros, tick, account);
            Interlocked.Exchange(ref capturedWrites, w); capturedTick = tick; capturedPaused = paused;
            if (w == Interlocked.Read(ref writes)) deferredAt = 0;
            capturedAt = Stopwatch.GetTimestamp(); lastCaptureMicros = captureMicros;
            if (frame == null) { Interlocked.Exchange(ref pending, 0); return; }
            Task.Factory.StartNew(() =>
            {
                try { r.Publish(frame, w, captureMicros); }
                catch (Exception e) { Log.WarningOnce("[RimGovernor] snapshot frame publish failed: " + e.Message, 0x5e859); }
                finally { Interlocked.Exchange(ref pending, 0); }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        /// A capture past this many microseconds of game thread is logged
        /// with its per-family breakdown, at most once per SlowLogTicks.
        internal const uint SlowCaptureMicros = 20_000;
        private const long DutyCycle = 10;
        private static long capturedAt;
        private static uint lastCaptureMicros;
        private const int SlowLogTicks = 2500;
        private static int slowLoggedTick = -SlowLogTicks;

        // NoteSlowCapture logs where a slow frame capture's game-thread time
        // went, family by family (#858), so the capture can be profiled from
        // any run's Player.log.
        private static void NoteSlowCapture(uint micros, int tick, ObservationWork.Hop? hop)
        {
            if (micros < SlowCaptureMicros || hop == null || (tick >= slowLoggedTick && tick - slowLoggedTick < SlowLogTicks)) return;
            slowLoggedTick = tick;
            var parts = new System.Text.StringBuilder();
            foreach (var s in hop.Sections.OrderByDescending(s => s.Ticks))
                parts.Append(' ').Append(s.Name).Append('=').Append((s.Ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("ms/").Append(s.Rows);
            Log.Message("[RimGovernor] snapshot frame capture " + (micros / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "ms at tick " + tick + ":" + parts);
        }

        // Elapsed microseconds since a Stopwatch timestamp, saturated.
        private static uint Micros(long started)
        {
            var micros = (Stopwatch.GetTimestamp() - started) * 1_000_000 / Stopwatch.Frequency;
            return (uint)Math.Min(Math.Max(micros, 0), uint.MaxValue);
        }

        private sealed class Ring
        {
            internal readonly string Name;
            private readonly MemoryMappedFile mapping;
            private readonly MemoryMappedViewAccessor view;
            private readonly FileStream? file;
            private readonly EventWaitHandle? ready;
            private readonly object writer = new object();
            private ulong head;

            internal Ring()
            {
                long capacity = HeaderBytes + (long)Slots * SlotBytes;
                var unique = Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
                if (Application.platform == RuntimePlatform.LinuxPlayer)
                {
                    SweepDeadRings();
                    Name = ShmDir + "/" + Prefix + unique;
                    file = new FileStream(Name, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                    file.SetLength(capacity);
                    mapping = MemoryMappedFile.CreateFromFile(file, null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                    var own = Name;
                    Application.quitting += () => { try { File.Delete(own); } catch (Exception) { } };
                }
                else
                {
                    Name = "Local\\" + Prefix + unique;
                    mapping = MemoryMappedFile.CreateNew(Name, capacity);
                    ready = new EventWaitHandle(false, EventResetMode.ManualReset, Name + "-ready");
                }
                view = mapping.CreateViewAccessor();
                view.Write(0, Magic);
                view.Write(4, Version);
                view.Write(8, (uint)Slots);
                view.Write(12, (uint)SlotBytes);
                view.Write(16, 0UL);
                view.Write(24, 0L);
            }

            private const string ShmDir = "/dev/shm", Prefix = "RimGovernorSnapshot-";

            // Deletes the ring files of processes that are gone (a killed
            // game never runs its quit hook). A name without a pid is left.
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
                catch (Exception e) { Log.WarningOnce("[RimGovernor] snapshot ring sweep failed: " + e.Message, 0x5e85b); }
            }

            internal void WriteHeader(long offset, long value) { view.Write(offset, value); Thread.MemoryBarrier(); }

            // On an encoder worker. A frame over the slot is dropped (logged
            // once); the controller then reads that family over GABP.
            internal void Publish(Obs.BundleSnapshot frame, long capturedAt, uint captureMicros)
            {
                var encodeStarted = Stopwatch.GetTimestamp();
                var payload = frame.ToByteArray();
                var encodeMicros = Micros(encodeStarted);
                if (payload.Length > SlotBytes - SlotHeaderBytes)
                {
                    Log.WarningOnce("[RimGovernor] snapshot frame of " + payload.Length + " bytes exceeds the " + SlotBytes + "-byte slot; not published.", 0x5e85a);
                    return;
                }
                lock (writer)
                {
                    var writeStarted = Stopwatch.GetTimestamp();
                    var n = head + 1;
                    long slot = HeaderBytes + (long)(n % Slots) * SlotBytes;
                    ready?.Reset();
                    view.Write(slot, 2 * n + 1);
                    Thread.MemoryBarrier();
                    view.Write(slot + 8, capturedAt);
                    view.Write(slot + 16, (uint)payload.Length);
                    view.Write(slot + 20, captureMicros);
                    view.Write(slot + 24, encodeMicros);
                    bool retained = false;
                    var handle = view.SafeMemoryMappedViewHandle;
                    try
                    {
                        handle.DangerousAddRef(ref retained);
                        Marshal.Copy(payload, 0, IntPtr.Add(handle.DangerousGetHandle(), (int)(slot + SlotHeaderBytes)), payload.Length);
                    }
                    finally { if (retained) handle.DangerousRelease(); }
                    // The copy and header writes; the commit below is a few stores.
                    view.Write(slot + 28, Micros(writeStarted));
                    Thread.MemoryBarrier();
                    view.Write(slot, 2 * n);
                    Thread.MemoryBarrier();
                    view.Write(16, n);
                    head = n;
                    ready?.Set();
                }
            }
        }
    }
}
