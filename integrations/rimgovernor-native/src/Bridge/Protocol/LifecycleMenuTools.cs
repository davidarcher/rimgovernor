using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace RimGovernor.Native
{
    // Return to the main menu in the running process: ends the loaded game
    // without saving exactly as the player's quit-to-menu does
    // (GenScene.GoToMainMenu: the game is disposed, then a queued long event
    // clears the maps and world and loads the Entry scene). The process and the
    // in-process brain stay up. Menu starts it and returns MenuPending; the
    // caller polls ReadMenu until the game is at a fresh main menu -- the state
    // LifecycleNewColonyTools starts from (Entry program state, no game,
    // no queued long event). Already at a fresh main menu, Menu completes at
    // once with unloaded=false.
    internal static class LifecycleMenuTools
    {
        private const int MaxEntries = 32;

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private static string? activeRequestId;

        private sealed class Entry
        {
            public readonly string RequestId;
            public readonly DateTime StartedUtc = DateTime.UtcNow;
            public RimGovernor.Brain.GameFacts.Lifecycle.MenuReply? Reply; // Terminal outcome; null while the unload is running.
            public Entry(string requestId) { RequestId = requestId; }
        }

        private static RimGovernor.Brain.GameFacts.Lifecycle.MenuReply Fail(RimGovernor.Brain.GameFacts.Common.FailureCode code, string detail) =>
            new RimGovernor.Brain.GameFacts.Lifecycle.MenuReply { Outcome = GameThread.Fail(code, detail) };

        // A fresh main menu: what a new colony requires before it starts.
        private static bool AtFreshMenu() =>
            Current.ProgramState == ProgramState.Entry && Current.Game == null && Find.CurrentMap == null
            && !LongEventHandler.AnyEventNowOrWaiting;

        // Call only on the game thread.
        internal static RimGovernor.Brain.GameFacts.Lifecycle.MenuReply StartMenu(RimGovernor.Brain.GameOrders.Lifecycle.MenuRequest request)
        {
            if (request == null || request.RequestId is null || !GameThread.IsIdentifier(request.RequestId))
                return Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Menu requires a request id.");
            var id = request.RequestId;

            lock (Lock)
            {
                Prune();
                // A retry of a request already answered or still running answers its status.
                if (Entries.TryGetValue(id, out var known))
                    return known.Reply ?? PendingReply(known, "Unloading the game.");
                if (activeRequestId != null && Entries.TryGetValue(activeRequestId, out var previous) && previous.Reply == null)
                    return Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "A return to the main menu (" + previous.RequestId + ") is already running.");
            }

            if (AtFreshMenu())
                return Record(new Entry(id), unloaded: false);

            // GoToMainMenu clears queued long events, so it would drop a load, a
            // new colony phase or a save already queued: refuse instead.
            if (LongEventHandler.AnyEventNowOrWaiting)
                return Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The game is busy with another long event.");
            if (Current.Game == null)
                return Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "No game is loaded, but the game is not at the main menu yet.");

            // Ending a live colony requires the caller to name it, as replacing
            // a live map by Load does.
            if (Find.CurrentMap != null && GameThread.TryReadContext(Find.CurrentMap, out var current, out _))
            {
                var expected = request.ExpectedPlayer;
                if (expected == null || expected.Identity == null || !expected.Identity.Equals(current.Identity)
                    || expected.PlayerDirection == 0)
                    return Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest,
                        "Ending a live map requires a matching expected player identity and direction.");
            }

            try
            {
                GenScene.GoToMainMenu();
            }
            catch (Exception error)
            {
                return Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NativeFailure, "Return to the main menu failed to start: " + error.GetType().Name);
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
        internal static RimGovernor.Brain.GameFacts.Lifecycle.MenuReply PollMenu(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus status)
        {
            if (status == null || status.RequestId is null || !GameThread.IsIdentifier(status.RequestId))
                return Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "ReadMenu requires a request id.");

            Entry? entry;
            lock (Lock)
            {
                if (!Entries.TryGetValue(status.RequestId, out entry))
                    return Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Unknown or expired menu request id.");
                if (entry.Reply is { } outcome)
                    return outcome;
            }

            if (!AtFreshMenu())
                return PendingReply(entry, "Unloading the game.");
            return Complete(entry, unloaded: true);
        }

        private static RimGovernor.Brain.GameFacts.Lifecycle.MenuReply PendingReply(Entry entry, string detail) =>
            new RimGovernor.Brain.GameFacts.Lifecycle.MenuReply { Outcome = new RimGovernor.Brain.GameFacts.Lifecycle.MenuPending { RequestId = entry.RequestId, Detail = detail } };

        private static RimGovernor.Brain.GameFacts.Lifecycle.MenuReply Complete(Entry entry, bool unloaded)
        {
            var reply = new RimGovernor.Brain.GameFacts.Lifecycle.MenuReply { Outcome = new RimGovernor.Brain.GameFacts.Lifecycle.MenuCompleted { RequestId = entry.RequestId, Unloaded = unloaded } };
            lock (Lock)
            {
                entry.Reply = reply;
            }
            return reply;
        }

        // An already-at-menu request is recorded completed, so ReadMenu answers it too.
        private static RimGovernor.Brain.GameFacts.Lifecycle.MenuReply Record(Entry entry, bool unloaded)
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
