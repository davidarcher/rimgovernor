using System.Collections.Generic;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Per-pawn eat-trigger overrides (#2794): the food-bar level below which the
    // pawn starts seeking food, by Thing.thingIDNumber. A pawn with no entry eats
    // by vanilla's 0.30. Only values below the vanilla trigger are stored.
    public sealed class EatTriggerState : GameComponent
    {
        public Dictionary<int, float> Triggers = new Dictionary<int, float>();
        public EatTriggerState(Game game) { }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref Triggers, "rimgovernorEatTriggers", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Triggers == null) Triggers = new Dictionary<int, float>();
        }
    }
}
