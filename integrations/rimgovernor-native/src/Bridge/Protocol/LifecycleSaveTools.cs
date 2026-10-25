using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;

namespace RimGovernor.Native
{
    // Trusted single-shot checkpoint save only. Native must already be paused;
    // The boundary owns pausing/draining writers before calling this tool.
    // Save itself is synchronous (completes or fails within one call); ReadSave
    // exists only so a caller who lost the original reply (a dropped
    // connection, a client restart) can re-read that request's exact outcome
    // by request_id, never to poll an in-progress save. Reconnect session
    // semantics, competing viewers and media/control ownership are separate,
    // unimplemented capability.
    internal static class LifecycleSaveTools
    {

        // Bounded in-memory table of recent save outcomes, guarded by Lock, so
        // ReadSave can answer a request_id whose original reply the caller
        // never received. Pruned oldest-first once over MaxEntries.
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();
        private const int MaxEntries = 32;

        private sealed class Entry
        {
            public readonly string RequestId;
            public readonly DateTime RecordedUtc;
            public readonly RimGovernor.Brain.GameFacts.Lifecycle.SaveReply Reply;
            public Entry(string requestId, DateTime recordedUtc, RimGovernor.Brain.GameFacts.Lifecycle.SaveReply reply) { RequestId = requestId; RecordedUtc = recordedUtc; Reply = reply; }
        }

        // Call only on the game thread.
        internal static RimGovernor.Brain.GameFacts.Lifecycle.SaveReply Apply(RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest request)
        {
            var player = request.Player;
            if (player == null || !ValidIdentity(player.Identity) || player.PlayerDirection == 0
                || player.RequestId is null || !GameThread.IsIdentifier(player.RequestId)
                || request.SaveName is null || !GameThread.IsIdentifier(request.SaveName))
                return new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest,
                    "Save requires a complete player identity, direction, request id and save name.") };

            if (!GameThread.TryReadContext(Find.CurrentMap, out var context, out var unavailable))
                return new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, (unavailable.Detail ?? "")) };

            // A mismatch here is a race with the caller's own expectation, not a
            // malformed request: report it as uncertain and never save.
            if (!player.Identity.Equals(context.Identity) || (request.ExpectedTick is RimGovernor.Brain.GameOrders.Lifecycle.ExpectedSaveTick.AtCase { Value: var expectedTick } && expectedTick != context.Tick))
                return Record((player.RequestId ?? ""), new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = new RimGovernor.Brain.GameFacts.Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = request.SaveName, ObservedContext = context,
                    Detail = "Colony, load, map or tick changed before save."
                } });

            if (Find.TickManager == null || !Find.TickManager.Paused)
                return new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest,
                    "Native must already be paused before a trusted save.") };

            long bytes;
            try
            {
                bytes = WriteSave((request.SaveName ?? ""));
            }
            catch (Exception error)
            {
                ModLog.Warn("lifecycle", "native save " + (request.SaveName ?? "") + " failed: " + error);
                return new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NativeFailure,
                    "Native save failed: " + error.GetType().Name) };
            }

            if (!GameThread.TryReadContext(Find.CurrentMap, out var after, out var afterUnavailable))
                return Record((player.RequestId ?? ""), new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = new RimGovernor.Brain.GameFacts.Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = (request.SaveName ?? ""),
                    Detail = "Colony state could not be re-observed after save: " + (afterUnavailable.Detail ?? "")
                } });

            bool pausedAfter = Find.TickManager != null && Find.TickManager.Paused;
            if (!object.Equals(after.Identity, context.Identity) || after.Tick != context.Tick || !pausedAfter)
                return Record((player.RequestId ?? ""), new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = new RimGovernor.Brain.GameFacts.Lifecycle.SaveUncertain
                {
                    RequestId = player.RequestId, SaveName = (request.SaveName ?? ""), ObservedContext = after,
                    Detail = "Colony, load, map, tick or pause changed during save."
                } });

            return Record((player.RequestId ?? ""), new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = new RimGovernor.Brain.GameFacts.Lifecycle.SaveCompleted
            {
                RequestId = player.RequestId, SaveName = (request.SaveName ?? ""), Context = after,
                Paused = pausedAfter, ByteLength = (ulong)bytes, PlayerDirection = player.PlayerDirection
            } });
        }

        // The one native save path (lifecycle_save and the new-colony start):
        // vanilla SaveGame logs and swallows its own exceptions, so success is
        // the save file having been written. Returns its byte length; throws
        // when the file is missing, unchanged or empty. Game thread only.
        internal static long WriteSave(string saveName)
        {
            var path = GenFilePaths.FilePathForSavedGame(saveName);
            var before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            // Brain-initiated: the SaveGame prefix raises no pre_save signal.
            SaveHandshake.BeginGoSave();
            try { GameDataSaveLoader.SaveGame(saveName); }
            finally { SaveHandshake.EndGoSave(); }
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) == before)
                throw new IOException("The save file " + path + " was not written.");
            var length = new FileInfo(path).Length;
            if (length == 0) throw new IOException("The save file " + path + " is empty.");
            return length;
        }

        // Call only on the game thread.
        internal static RimGovernor.Brain.GameFacts.Lifecycle.SaveReply PollSave(RimGovernor.Brain.GameFacts.Lifecycle.RequestStatus status)
        {
            if (status == null || status.RequestId is null || !GameThread.IsIdentifier(status.RequestId))
                return new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest,
                    "ReadSave requires a request id.") };

            lock (Lock)
            {
                if (Entries.TryGetValue((status.RequestId ?? ""), out var entry))
                    return entry.Reply;
            }
            return new RimGovernor.Brain.GameFacts.Lifecycle.SaveReply { Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound,
                "Unknown or expired save request id.") };
        }

        private static RimGovernor.Brain.GameFacts.Lifecycle.SaveReply Record(string requestId, RimGovernor.Brain.GameFacts.Lifecycle.SaveReply reply)
        {
            lock (Lock)
            {
                Prune();
                Entries[requestId] = new Entry(requestId, DateTime.UtcNow, reply);
            }
            return reply;
        }

        private static void Prune()
        {
            if (Entries.Count < MaxEntries) return;
            string? oldest = null;
            var oldestTime = DateTime.MaxValue;
            foreach (var pair in Entries)
            {
                if (pair.Value.RecordedUtc < oldestTime)
                {
                    oldest = pair.Key;
                    oldestTime = pair.Value.RecordedUtc;
                }
            }
            if (oldest != null)
                Entries.Remove(oldest);
        }

        private static bool ValidIdentity([NotNullWhen(true)] RimGovernor.Brain.GameFacts.Common.Identity? identity) =>
            identity != null && identity.ColonyId is not null && identity.LoadToken is not null
            && GameThread.IsIdentifier(identity.ColonyId) && GameThread.IsIdentifier(identity.LoadToken) && identity.MapId >= 0;
    }
}
