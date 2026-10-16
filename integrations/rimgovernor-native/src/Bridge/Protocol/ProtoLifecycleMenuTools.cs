#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // Return to the main menu in the running process: ends the loaded game
    // without saving exactly as the player's quit-to-menu does
    // (GenScene.GoToMainMenu: the game is disposed, then a queued long event
    // clears the maps and world and loads the Entry scene). The process and the
    // in-process brain stay up. Menu starts it and returns MenuPending; the
    // caller polls ReadMenu until the game is at a fresh main menu -- the state
    // ProtoLifecycleNewColonyTools starts from (Entry program state, no game,
    // no queued long event). Already at a fresh main menu, Menu completes at
    // once with unloaded=false.
    public sealed class ProtoLifecycleMenuTools
    {
        private const string MenuToolName = "rimgovernor/lifecycle_menu";
        private const string ReadMenuToolName = "rimgovernor/lifecycle_read_menu";
        private const int MaxEntries = 32;

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private static string? activeRequestId;

        private sealed class Entry
        {
            public readonly string RequestId;
            public readonly DateTime StartedUtc = DateTime.UtcNow;
            public Lifecycle.MenuReply? Reply; // Terminal outcome; null while the unload is running.
            public Entry(string requestId) { RequestId = requestId; }
        }

        [Tool(MenuToolName, Title = "Return to the main menu",
            Description = "End the loaded game without saving and return the running process to a fresh main menu. Returns MenuPending; poll rimgovernor/lifecycle_read_menu.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.MenuReply.", Always = true)]
        public async Task<object> Menu(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle MenuRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Lifecycle.MenuRequest>(ctx, MenuToolName, request, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.MenuReply { Outcome = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => StartMenu(parsed), cancellationToken).ConfigureAwait(false);
        }

        [Tool(ReadMenuToolName, Title = "Read a return to the main menu",
            Description = "Poll a request_id from rimgovernor/lifecycle_menu for MenuCompleted/MenuPending/Failure.")]
        [ToolResponse("payload", "string", "Official ProtoJSON rimgovernor.lifecycle.v1.MenuReply.", Always = true)]
        public async Task<object> ReadMenu(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official lifecycle RequestStatus ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Lifecycle.RequestStatus>(ctx, ReadMenuToolName, request, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Lifecycle.MenuReply { Outcome = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => PollMenu(parsed), cancellationToken).ConfigureAwait(false);
        }

        private static Lifecycle.MenuReply Fail(Common.FailureCode code, string detail) =>
            new Lifecycle.MenuReply { Outcome = ProtoBoundary.Fail(code, detail) };

        // A fresh main menu: what a new colony requires before it starts.
        private static bool AtFreshMenu() =>
            Current.ProgramState == ProgramState.Entry && Current.Game == null && Find.CurrentMap == null
            && !LongEventHandler.AnyEventNowOrWaiting;

        // Call only on the game thread.
        internal static Lifecycle.MenuReply StartMenu(Lifecycle.MenuRequest request)
        {
            if (request == null || request.RequestId is null || !ProtoBoundary.IsIdentifier(request.RequestId))
                return Fail(Common.FailureCode.InvalidRequest, "Menu requires a request id.");
            var id = request.RequestId;

            lock (Lock)
            {
                Prune();
                // A retry of a request already answered or still running answers its status.
                if (Entries.TryGetValue(id, out var known))
                    return known.Reply ?? PendingReply(known, "Unloading the game.");
                if (activeRequestId != null && Entries.TryGetValue(activeRequestId, out var previous) && previous.Reply == null)
                    return Fail(Common.FailureCode.Unavailable, "A return to the main menu (" + previous.RequestId + ") is already running.");
            }

            if (AtFreshMenu())
                return Record(new Entry(id), unloaded: false);

            // GoToMainMenu clears queued long events, so it would drop a load, a
            // new colony phase or a save already queued: refuse instead.
            if (LongEventHandler.AnyEventNowOrWaiting)
                return Fail(Common.FailureCode.Unavailable, "The game is busy with another long event.");
            if (Current.Game == null)
                return Fail(Common.FailureCode.Unavailable, "No game is loaded, but the game is not at the main menu yet.");

            // Ending a live colony requires the caller to name it, as replacing
            // a live map by Load does.
            if (Find.CurrentMap != null && ProtoBoundary.TryReadContext(Find.CurrentMap, out var current, out _))
            {
                var expected = request.ExpectedPlayer;
                if (expected == null || expected.Identity == null || !expected.Identity.Equals(current.Identity)
                    || expected.PlayerDirection is null || expected.PlayerDirection.GetValueOrDefault() == 0)
                    return Fail(Common.FailureCode.InvalidRequest,
                        "Ending a live map requires a matching expected player identity and direction.");
            }

            try
            {
                GenScene.GoToMainMenu();
            }
            catch (Exception error)
            {
                return Fail(Common.FailureCode.NativeFailure, "Return to the main menu failed to start: " + error.GetType().Name);
            }

            var entry = new Entry(id);
            lock (Lock)
            {
                Entries[entry.RequestId] = entry;
                activeRequestId = entry.RequestId;
            }
            return PendingReply(entry, "Unloading the game.");
        }

        // Call only on the game thread.
        internal static Lifecycle.MenuReply PollMenu(Lifecycle.RequestStatus status)
        {
            if (status == null || status.RequestId is null || !ProtoBoundary.IsIdentifier(status.RequestId))
                return Fail(Common.FailureCode.InvalidRequest, "ReadMenu requires a request id.");

            Entry? entry;
            lock (Lock)
            {
                if (!Entries.TryGetValue(status.RequestId, out entry))
                    return Fail(Common.FailureCode.NotFound, "Unknown or expired menu request id.");
                if (entry.Reply is { } outcome)
                    return outcome;
            }

            if (!AtFreshMenu())
                return PendingReply(entry, "Unloading the game.");
            return Complete(entry, unloaded: true);
        }

        private static Lifecycle.MenuReply PendingReply(Entry entry, string detail) =>
            new Lifecycle.MenuReply { Outcome = new Lifecycle.MenuPending { RequestId = entry.RequestId, Detail = detail } };

        private static Lifecycle.MenuReply Complete(Entry entry, bool unloaded)
        {
            var reply = new Lifecycle.MenuReply { Outcome = new Lifecycle.MenuCompleted { RequestId = entry.RequestId, Unloaded = unloaded } };
            lock (Lock)
            {
                entry.Reply = reply;
            }
            return reply;
        }

        // An already-at-menu request is recorded completed, so ReadMenu answers it too.
        private static Lifecycle.MenuReply Record(Entry entry, bool unloaded)
        {
            lock (Lock)
            {
                Entries[entry.RequestId] = entry;
            }
            return Complete(entry, unloaded);
        }

        private static void Prune()
        {
            if (Entries.Count <= MaxEntries) return;
            string? oldest = null;
            var oldestTime = DateTime.MaxValue;
            foreach (var pair in Entries)
            {
                if (pair.Value.Reply != null && pair.Value.StartedUtc < oldestTime && pair.Key != activeRequestId)
                {
                    oldest = pair.Key;
                    oldestTime = pair.Value.StartedUtc;
                }
            }
            if (oldest != null)
                Entries.Remove(oldest);
        }
    }
}
