#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Verse;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace HomeBridge.BridgeTools
{
    internal static class ProtoGovernorStateTools
    {
        // On the game thread: the typed core of the read, shared with the in-process facade.
        internal static Lifecycle.GovernorStateReply HandleRead() => Reply();

        // Off the game thread: validates the batch and stages it when the game already carries the component.
        // Null means a save that predates the component needs the game thread to attach it: PutOnGameThread(set).
        internal static Lifecycle.GovernorStateReply? TryPut(Lifecycle.PutGovernorStateBatchRequest request, out System.Collections.Generic.Dictionary<string, string> set)
        {
            // An empty blob means absent.
            set = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var pair in request.Blobs)
            {
                if (string.IsNullOrEmpty(pair.Key) || !Ascii(pair.Key) || !Ascii(pair.Value))
                {
                    ReadFailureLog.Note("governor_state_put", "key must be non-empty ASCII and blob ASCII");
                    return new Lifecycle.GovernorStateReply();
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
        internal static Lifecycle.GovernorStateReply PutOnGameThread(System.Collections.Generic.Dictionary<string, string> set)
        {
            if (Current.Game == null) return NotLoaded("governor_state_put");
            GovernorState.For(Current.Game).Stage(set);
            return Staged();
        }

        private static Lifecycle.GovernorStateReply Staged() => new Lifecycle.GovernorStateReply { Loaded = new Lifecycle.GovernorStateBlobs() };

        private static Lifecycle.GovernorStateReply NotLoaded(string readKind)
        {
            ReadFailureLog.Note(readKind, "No game is loaded.");
            return new Lifecycle.GovernorStateReply();
        }

        private static bool Ascii(string value) => value.All(c => c < 128);

        private static Lifecycle.GovernorStateReply Reply()
        {
            if (Current.Game == null) return NotLoaded("governor_state_read");
            var loaded = new Lifecycle.GovernorStateBlobs { Blobs = new System.Collections.Generic.Dictionary<string, string>(GovernorState.For(Current.Game).Blobs) };
            return new Lifecycle.GovernorStateReply { Loaded = loaded };
        }
    }
}
