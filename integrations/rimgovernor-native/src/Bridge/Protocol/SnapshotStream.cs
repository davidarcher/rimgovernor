#nullable enable
using System;
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
    /// edge and after every applied write; an encoder worker formats it and
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
    ///     16 uint32 payload length    20 reserved
    ///     24 payload: BundleSnapshot binary
    /// The ready event (Windows only; a Linux reader polls) is name + "-ready",
    /// a manual-reset event set after each commit.
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
            return await ProtoBoundary.OnMainThread(ctx, () => ProtoBoundary.Encode(SnapshotStream.Open(parsed)), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static class SnapshotStream
    {
        internal const int Slots = 3;
        internal const int SlotBytes = 32 << 20;
        internal const int HeaderBytes = 64, SlotHeaderBytes = 24;
        internal const int PeriodTicks = 60;
        private const uint Magic = 0x53534752; // "RGSS"
        private const uint Version = 1;

        private static readonly object Gate = new object();
        private static Ring? ring;
        private static Obs.SnapshotStreamRequest subscription = new Obs.SnapshotStreamRequest();
        // Game-thread state: what the last capture saw.
        private static long writes, capturedWrites = -1;
        private static int capturedTick = int.MinValue;
        private static bool capturedPaused;
        private static int pending; // captures handed to the encoder, not yet written

        /// On the game thread: an applied write the next frame must reflect.
        internal static void NoteWrite()
        {
            var r = ring;
            var value = Interlocked.Increment(ref writes);
            r?.WriteHeader(24, value);
        }

        internal static Obs.SnapshotStreamReply Open(Obs.SnapshotStreamRequest request)
        {
            lock (Gate)
            {
                try { ring ??= new Ring(); }
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
            if (w == capturedWrites && paused == capturedPaused && tick - capturedTick < PeriodTicks && tick >= capturedTick) return;
            // One frame in flight at a time: a slow encode delays the next
            // capture instead of queueing stale ones.
            if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;
            Obs.BundleSnapshot? frame;
            Obs.SnapshotStreamRequest shape;
            lock (Gate) shape = subscription;
            try { frame = NativeBundleTools.CaptureFrame(map, Request(shape)); }
            catch (Exception e)
            {
                Interlocked.Exchange(ref pending, 0);
                Log.WarningOnce("[RimGovernor] snapshot frame capture failed: " + e.Message, 0x5e858);
                return;
            }
            capturedWrites = w; capturedTick = tick; capturedPaused = paused;
            if (frame == null) { Interlocked.Exchange(ref pending, 0); return; }
            Task.Factory.StartNew(() =>
            {
                try { r.Publish(frame, w); }
                catch (Exception e) { Log.WarningOnce("[RimGovernor] snapshot frame publish failed: " + e.Message, 0x5e859); }
                finally { Interlocked.Exchange(ref pending, 0); }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        // Every state family the bundle can read, whole, plus the
        // subscription's parameterized families; no clock status or events.
        private static Obs.BundleRequest Request(Obs.SnapshotStreamRequest shape)
        {
            var request = new Obs.BundleRequest
            {
                Emergency = true, ColonyFacts = true, Population = true, Research = true, ColonistPawns = true,
                Buildings = true, BuiltBuildings = true, Bills = true, Zones = true, Traders = true, WorldProgression = true,
            };
            request.ResourceSources.Add(shape.ResourceSources);
            if (shape.PlanningWindow != null) request.PlanningWindow = new Obs.BundlePlanningWindowRequest { Region = shape.PlanningWindow.Clone() };
            return request;
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
                if (Application.platform == RuntimePlatform.LinuxPlayer)
                {
                    Name = "/dev/shm/RimGovernorSnapshot-" + Guid.NewGuid().ToString("N");
                    file = new FileStream(Name, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                    file.SetLength(capacity);
                    mapping = MemoryMappedFile.CreateFromFile(file, null, capacity, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                }
                else
                {
                    Name = "Local\\RimGovernorSnapshot-" + Guid.NewGuid().ToString("N");
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

            internal void WriteHeader(long offset, long value) { view.Write(offset, value); Thread.MemoryBarrier(); }

            // On an encoder worker. A frame over the slot is dropped (logged
            // once); the controller then reads that family over GABP.
            internal void Publish(Obs.BundleSnapshot frame, long capturedAt)
            {
                var payload = frame.ToByteArray();
                if (payload.Length > SlotBytes - SlotHeaderBytes)
                {
                    Log.WarningOnce("[RimGovernor] snapshot frame of " + payload.Length + " bytes exceeds the " + SlotBytes + "-byte slot; not published.", 0x5e85a);
                    return;
                }
                lock (writer)
                {
                    var n = head + 1;
                    long slot = HeaderBytes + (long)(n % Slots) * SlotBytes;
                    ready?.Reset();
                    view.Write(slot, 2 * n + 1);
                    Thread.MemoryBarrier();
                    view.Write(slot + 8, capturedAt);
                    view.Write(slot + 16, (uint)payload.Length);
                    bool retained = false;
                    var handle = view.SafeMemoryMappedViewHandle;
                    try
                    {
                        handle.DangerousAddRef(ref retained);
                        Marshal.Copy(payload, 0, IntPtr.Add(handle.DangerousGetHandle(), (int)(slot + SlotHeaderBytes)), payload.Length);
                    }
                    finally { if (retained) handle.DangerousRelease(); }
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
