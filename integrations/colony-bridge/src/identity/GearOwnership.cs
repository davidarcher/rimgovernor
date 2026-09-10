using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Save components must be available before the optional bridge tools load.
    public sealed class GearOwnership : GameComponent
    {
        public Dictionary<string, string> Weapons = new Dictionary<string, string>();
        public GearOwnership(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Weapons, "rimgovernorUpkeepWeapons", LookMode.Value, LookMode.Value);
            if (Weapons == null) Weapons = new Dictionary<string, string>();
        }
        public static GearOwnership State()
        {
            var state = Current.Game.GetComponent<GearOwnership>();
            if (state == null) { state = new GearOwnership(Current.Game); Current.Game.components.Add(state); }
            return state;
        }
    }
}
