#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
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
                    return new Lifecycle.GovernorStateReply { Outcome = new Common.Failure {
                        Code = Common.FailureCode.InvalidRequest, Detail = "key must be non-empty ASCII and blob ASCII" } };
                if (pair.Value.Length > 0) set[pair.Key] = pair.Value;
            }
            var game = Current.Game;
            if (game == null) return NotLoaded();
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
            if (Current.Game == null) return NotLoaded();
            GovernorState.For(Current.Game).Stage(set);
            return Staged();
        }

        private static Lifecycle.GovernorStateReply Staged() => new Lifecycle.GovernorStateReply { Outcome = new Lifecycle.GovernorStateBlobs() };

        private static Lifecycle.GovernorStateReply NotLoaded() =>
            new Lifecycle.GovernorStateReply { Outcome = new Common.Unavailable {
                Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };

        private static bool Ascii(string value) => value.All(c => c < 128);

        private static Lifecycle.GovernorStateReply Reply()
        {
            if (Current.Game == null)
                return new Lifecycle.GovernorStateReply { Outcome = new Common.Unavailable {
                    Reason = Common.UnavailableReason.NotLoaded, Detail = "No game is loaded." } };
            var loaded = new Lifecycle.GovernorStateBlobs { Blobs = new System.Collections.Generic.Dictionary<string, string>(GovernorState.For(Current.Game).Blobs) };
            return new Lifecycle.GovernorStateReply { Outcome = loaded };
        }
    }
}
