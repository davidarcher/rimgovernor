using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using Verse;

namespace RimGovernor.Native
{
    internal static class GovernorStateTools
    {
        // On the game thread: the read of the in-process facade. No game throws the not-loaded exception.
        internal static RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateBlobs HandleRead()
        {
            if (Current.Game == null)
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." });
            return new RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateBlobs { Blobs = new System.Collections.Generic.Dictionary<string, string>(GovernorState.For(Current.Game).Blobs) };
        }

        // Off the game thread: validates the batch and stages it when the game already carries the component.
        // Null means a save that predates the component needs the game thread to attach it: PutOnGameThread(set).
        internal static RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply? TryPut(RimGovernor.Brain.GameOrders.Lifecycle.PutGovernorStateBatchRequest request, out System.Collections.Generic.Dictionary<string, string> set)
        {
            // An empty blob means absent.
            set = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var pair in request.Blobs)
            {
                if (string.IsNullOrEmpty(pair.Key) || !Ascii(pair.Key) || !Ascii(pair.Value))
                {
                    ReadFailureLog.Note("governor_state_put", "key must be non-empty ASCII and blob ASCII");
                    return new RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply();
                }
                if (pair.Value.Length > 0) set[pair.Key] = pair.Value;
            }
            var game = Current.Game;
            if (game == null) return NotLoaded("governor_state_put");
            // Staging is lock-guarded, so the worker thread never waits for the
            // frame loop. Only a save that predates the component needs the
            // game thread, to attach it.
            var state = game.GetComponent<GovernorState>();
            if (state == null) return null;
            state.Stage(set);
            return Staged();
        }

        // On the game thread: attaches the component to an older save and stages the batch.
        internal static RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply PutOnGameThread(System.Collections.Generic.Dictionary<string, string> set)
        {
            if (Current.Game == null) return NotLoaded("governor_state_put");
            GovernorState.For(Current.Game).Stage(set);
            return Staged();
        }

        private static RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply Staged() => new RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply { Loaded = new RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateBlobs() };

        private static RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply NotLoaded(string readKind)
        {
            ReadFailureLog.Note(readKind, "No game is loaded.");
            return new RimGovernor.Brain.GameFacts.Lifecycle.GovernorStateReply();
        }

        private static bool Ascii(string value) => value.All(c => c < 128);
    }
}
