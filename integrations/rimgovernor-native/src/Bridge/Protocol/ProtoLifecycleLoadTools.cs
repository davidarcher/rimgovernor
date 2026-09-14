using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // Trusted native load of a named save into the already-running, already-
    // connected game process (not a wrapper around the legacy GABS
    // rimworld/load_game_ready tool, and not a relaunch: this calls
    // GameDataSaveLoader directly, same as ProtoLifecycleSaveTools calls
    // SaveGame directly). Loading is asynchronous: Load starts it and
    // returns LoadPending; the caller polls ReadLoad for either MAP readiness
    // (a live Find.CurrentMap with a valid identity) or, when requested,
    // VISUAL readiness (MAP readiness plus at least one actual map draw --
    // see MapVisualReadyTracker below). Reconnect-after-disconnect, competing
    // viewers and camera/input are separate, unimplemented capability.
    public sealed class ProtoLifecycleLoadTools
    {
        private const string LoadToolName = "rimgovernor/lifecycle_load";
        private const string ReadLoadToolName = "rimgovernor/lifecycle_read_load";

        // Bounded in-memory table of in-flight/recent load requests, guarded by
        // Lock. Only one load is ever actually "active" (native only ever calls
        // GameDataSaveLoader.LoadGame once at a time); older entries are kept
        // just long enough to answer ReadLoad for a request a newer Load call
        // superseded, then pruned.
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private static string activeRequestId;
        private const int MaxEntries = 32;

        private sealed class Entry
        {
            public string RequestId;
            public string SaveName;
            public Common.Identity ExpectedColonyIdentity; // may be null: no live map existed before this load.
            public DateTime StartedUtc;
            public uint? TimeoutMs;
            public Lifecycle.Readiness RequestedReadiness;
            public bool Completed;
            public Lifecycle.LoadReply Reply; // Set only once Completed is true.
        }

        [Tool(LoadToolName, Title = "Start a native load",
            Description = "Start loading a named save into the running process. Returns LoadPending; poll rimgovernor/lifecycle_read_load for completion.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.LoadReply.", Always = true)]
        public async Task<object> Load(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle LoadRequest ProtoJSON string.")] object request = null)
        {
            Lifecycle.LoadRequest parsed;
            Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, LoadToolName, request, Lifecycle.LoadRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Lifecycle.LoadReply { Failure = failure });

            var reply = await ctx.MainThread.InvokeAsync(() => StartLoad(parsed), cancellationToken).ConfigureAwait(false);
            return ProtoBoundary.Encode(reply);
        }

        [Tool(ReadLoadToolName, Title = "Read a native load's progress",
            Description = "Poll a request_id from rimgovernor/lifecycle_load for LoadCompleted/LoadPending/LoadSuperseded/Failure.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.LoadReply.", Always = true)]
        public async Task<object> ReadLoad(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle RequestStatus ProtoJSON string.")] object request = null)
        {
            Lifecycle.RequestStatus parsed;
            Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, ReadLoadToolName, request, Lifecycle.RequestStatus.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Lifecycle.LoadReply { Failure = failure });

            var reply = await ctx.MainThread.InvokeAsync(() => PollLoad(parsed), cancellationToken).ConfigureAwait(false);
            return ProtoBoundary.Encode(reply);
        }

        // Call only on the game thread.
        internal static Lifecycle.LoadReply StartLoad(Lifecycle.LoadRequest request)
        {
            if (request == null || !request.HasRequestId || !ProtoBoundary.IsIdentifier(request.RequestId)
                || !request.HasSaveName || !ProtoBoundary.IsIdentifier(request.SaveName))
                return new Lifecycle.LoadReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                    "Load requires a request id and save name.") };

            var expectedPlayer = request.ExpectedPlayer;
            Common.Identity expectedColonyIdentity = null;

            // A live map already exists: this load would replace it. Refuse up
            // front unless the caller's identity/direction still matches what is
            // currently live -- before a map exists, GABS-layer instance
            // ownership already gated the call, per the request's own comment.
            if (Find.CurrentMap != null && Current.Game != null)
            {
                Common.ObservationContext current;
                Common.Unavailable currentUnavailable;
                if (ProtoBoundary.TryReadContext(Find.CurrentMap, out current, out currentUnavailable))
                {
                    if (expectedPlayer == null || expectedPlayer.Identity == null
                        || !expectedPlayer.Identity.Equals(current.Identity)
                        || !expectedPlayer.HasPlayerDirection || expectedPlayer.PlayerDirection == 0)
                        return new Lifecycle.LoadReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                            "Replacing a live map requires a matching expected player identity and direction.") };
                    expectedColonyIdentity = current.Identity;
                }
            }

            lock (Lock)
            {
                Prune();
                // A prior Load is still in flight under a different request id:
                // that older request is now superseded. It keeps its entry so a
                // caller still polling it learns what happened instead of
                // waiting forever.
                if (activeRequestId != null && activeRequestId != request.RequestId
                    && Entries.TryGetValue(activeRequestId, out var previous) && !previous.Completed)
                {
                    previous.Completed = true;
                    previous.Reply = new Lifecycle.LoadReply { Superseded = new Lifecycle.LoadSuperseded
                    {
                        RequestId = previous.RequestId,
                        Detail = "A newer rimgovernor/lifecycle_load request (" + request.RequestId + ") started before this one completed."
                    } };
                }
            }

            try
            {
                GameDataSaveLoader.LoadGame(request.SaveName);
            }
            catch (Exception error)
            {
                return new Lifecycle.LoadReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure,
                    "Native load failed to start: " + error.GetType().Name) };
            }

            var entry = new Entry
            {
                RequestId = request.RequestId,
                SaveName = request.SaveName,
                ExpectedColonyIdentity = expectedColonyIdentity,
                StartedUtc = DateTime.UtcNow,
                TimeoutMs = request.HasTimeoutMs ? (uint?)request.TimeoutMs : null,
                RequestedReadiness = request.HasReadiness && request.Readiness == Lifecycle.Readiness.Visual
                    ? Lifecycle.Readiness.Visual : Lifecycle.Readiness.Map,
            };
            lock (Lock)
            {
                Entries[entry.RequestId] = entry;
                activeRequestId = entry.RequestId;
            }

            return PendingReply(entry, mapReady: false, visualReady: false);
        }

        // Call only on the game thread.
        internal static Lifecycle.LoadReply PollLoad(Lifecycle.RequestStatus status)
        {
            if (status == null || !status.HasRequestId || !ProtoBoundary.IsIdentifier(status.RequestId))
                return new Lifecycle.LoadReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                    "ReadLoad requires a request id.") };

            Entry entry;
            lock (Lock)
            {
                if (!Entries.TryGetValue(status.RequestId, out entry))
                    return new Lifecycle.LoadReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NotFound,
                        "Unknown or expired load request id.") };
                if (entry.Completed)
                    return entry.Reply;
            }

            // LongEventHandler.QueueLongEvent runs the actual load across
            // subsequent frames; while it (or anything else) is queued or
            // running, the load is not done.
            if (LongEventHandler.AnyEventNowOrWaiting)
                return PendingReply(entry, mapReady: false, visualReady: false);

            Common.ObservationContext context;
            Common.Unavailable unavailable;
            if (!ProtoBoundary.TryReadContext(Find.CurrentMap, out context, out unavailable))
            {
                if (entry.TimeoutMs.HasValue && DateTime.UtcNow - entry.StartedUtc > TimeSpan.FromMilliseconds(entry.TimeoutMs.Value))
                    return CompleteWith(entry, new Lifecycle.LoadReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure,
                        "Native load did not reach a ready map within the requested timeout: " + unavailable.Detail) });
                return PendingReply(entry, mapReady: false, visualReady: false);
            }

            // Map is ready. A fresh load always issues a new load token; only
            // the colony id is expected to persist when reloading the same
            // colony's save. A different colony id means this observed load is
            // not the one this request believed it started.
            if (entry.ExpectedColonyIdentity != null && entry.ExpectedColonyIdentity.ColonyId != context.Identity.ColonyId)
                return CompleteWith(entry, new Lifecycle.LoadReply { Superseded = new Lifecycle.LoadSuperseded
                {
                    RequestId = entry.RequestId, ObservedContext = context,
                    Detail = "Observed colony after load does not match the identity this request expected."
                } });

            // MAP readiness is satisfied. A caller that asked for VISUAL
            // readiness additionally needs at least one real map draw after
            // this point -- MapVisualReadyTracker reports that from a Harmony
            // postfix on MapDrawer.DrawMapMesh, so this is the game actually
            // having rendered the loaded map at least once, not merely data
            // being in memory.
            bool visualReady = MapVisualReadyTracker.VisualReady(Find.CurrentMap);
            if (entry.RequestedReadiness == Lifecycle.Readiness.Visual && !visualReady)
            {
                if (entry.TimeoutMs.HasValue && DateTime.UtcNow - entry.StartedUtc > TimeSpan.FromMilliseconds(entry.TimeoutMs.Value))
                    return CompleteWith(entry, new Lifecycle.LoadReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure,
                        "Native load reached MAP readiness but not VISUAL readiness within the requested timeout.") });
                return PendingReply(entry, mapReady: true, visualReady: false);
            }

            bool paused = Find.TickManager != null && Find.TickManager.Paused;
            var readiness = entry.RequestedReadiness == Lifecycle.Readiness.Visual ? Lifecycle.Readiness.Visual : Lifecycle.Readiness.Map;
            return CompleteWith(entry, new Lifecycle.LoadReply { Completed = new Lifecycle.LoadCompleted
            {
                RequestId = entry.RequestId, SaveName = entry.SaveName,
                Loaded = new Lifecycle.LoadedIdentity { Context = context, Paused = paused },
                Readiness = readiness,
            } });
        }

        private static Lifecycle.LoadReply PendingReply(Entry entry, bool mapReady, bool visualReady) =>
            new Lifecycle.LoadReply { Pending = new Lifecycle.LoadPending
            {
                RequestId = entry.RequestId, SaveName = entry.SaveName,
                ProcessConnected = true, MapReady = mapReady, VisualReady = visualReady,
            } };

        private static Lifecycle.LoadReply CompleteWith(Entry entry, Lifecycle.LoadReply reply)
        {
            lock (Lock)
            {
                entry.Completed = true;
                entry.Reply = reply;
            }
            return reply;
        }

        private static void Prune()
        {
            if (Entries.Count <= MaxEntries) return;
            string oldest = null;
            var oldestTime = DateTime.MaxValue;
            foreach (var pair in Entries)
            {
                if (pair.Value.Completed && pair.Value.StartedUtc < oldestTime && pair.Key != activeRequestId)
                {
                    oldest = pair.Key;
                    oldestTime = pair.Value.StartedUtc;
                }
            }
            if (oldest != null)
                Entries.Remove(oldest);
        }
    }

    // VISUAL readiness means the game has actually drawn the loaded map at
    // least once, not merely that map data is in memory (MAP readiness).
    // MapDrawer.DrawMapMesh runs every frame a map is rendered, so a Harmony
    // postfix on it -- separate from RenderDemandDriver's own prefix patch on
    // the same method, which only ever skips a draw, never observes one --
    // is the simplest true signal. Everything here runs on the Unity main
    // thread (the same thread Draw and PollLoad both execute on via
    // ctx.MainThread.InvokeAsync), so no locking is needed.
    internal static class MapVisualReadyTracker
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Map> MapField =
            AccessTools.FieldRefAccess<MapDrawer, Map>("map");
        private static Harmony harmony;
        private static Map trackedMap;
        private static bool rendered;

        private static void EnsurePatched()
        {
            if (harmony != null) return;
            harmony = new Harmony("davidarcher.rimgovernor.lifecycle-visual-ready");
            harmony.Patch(AccessTools.Method(typeof(MapDrawer), "DrawMapMesh"),
                postfix: new HarmonyMethod(typeof(MapVisualReadyTracker), nameof(DrawPostfix)));
        }

        // Call only on the game thread. Reports whether `map` has been drawn
        // at least once since it most recently became the tracked map --
        // switching to a different map (a fresh load replacing an old one)
        // resets the signal, since a stale render of a since-unloaded map is
        // not evidence the newly loaded one has ever been drawn.
        internal static bool VisualReady(Map map)
        {
            EnsurePatched();
            if (map == null) return false;
            if (!ReferenceEquals(trackedMap, map))
            {
                trackedMap = map;
                rendered = false;
            }
            return rendered;
        }

        private static void DrawPostfix(MapDrawer __instance)
        {
            var map = __instance != null ? MapField(__instance) : null;
            if (map == null) return;
            if (!ReferenceEquals(trackedMap, map))
            {
                trackedMap = map;
                rendered = false;
            }
            rendered = true;
        }
    }
}
