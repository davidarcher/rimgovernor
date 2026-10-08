using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Governor state (#882): opaque blobs Go owns, saved with the game. Native
    // stores and returns them verbatim and never interprets them.
    public sealed class GovernorState : GameComponent
    {
        public Dictionary<string, string> Blobs = new Dictionary<string, string>();

        // A batched put (#2357) lands here from a bridge worker thread, never
        // touching Blobs (main thread only). The staged set replaces Blobs the
        // next time the game thread reads it or saves, so a main thread parked
        // on a save cannot deadlock the flush.
        private readonly object stagingLock = new object();
        private Dictionary<string, string>? staged;

        public GovernorState(Game game) { }

        // Safe from any thread. The whole set replaces the previous one; the
        // caller's dictionary is copied.
        public void Stage(IDictionary<string, string> set)
        {
            var copy = new Dictionary<string, string>(set);
            lock (stagingLock) staged = copy;
        }

        // Game thread only: fold a staged set into Blobs.
        public void ApplyStaged()
        {
            lock (stagingLock)
            {
                if (staged == null) return;
                Blobs = staged;
                staged = null;
            }
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) ApplyStaged();
            Scribe_Collections.Look(ref Blobs, "rimgovernorGovernorState", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Blobs == null) Blobs = new Dictionary<string, string>();
        }

        // Bridge assemblies can load after Verse cached GameComponent types, so
        // attach the component on first use (older saves carry none).
        public static GovernorState For(Game game)
        {
            var state = game.GetComponent<GovernorState>();
            if (state == null)
            {
                state = new GovernorState(game);
                game.components.Add(state);
            }
            state.ApplyStaged();
            if (state.Blobs == null) state.Blobs = new Dictionary<string, string>();
            return state;
        }
    }
}
