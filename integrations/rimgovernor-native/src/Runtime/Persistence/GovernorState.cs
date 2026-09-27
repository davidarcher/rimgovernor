using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Governor state (#882): opaque blobs Go owns, saved with the game. Native
    // stores and returns them verbatim and never interprets them.
    public sealed class GovernorState : GameComponent
    {
        public Dictionary<string, string> Blobs = new Dictionary<string, string>();
        public GovernorState(Game game) { }

        public override void ExposeData()
        {
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
            if (state.Blobs == null) state.Blobs = new Dictionary<string, string>();
            return state;
        }
    }
}
