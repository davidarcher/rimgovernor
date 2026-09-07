using System;
using Verse;
namespace HomeBridge.BridgeTools
{
    // Persist identity with the save; a load token is deliberately never serialized.
    public sealed class ColonyIdentity : GameComponent
    {
        public string ColonyId = Guid.NewGuid().ToString("N");
        public readonly string LoadToken = Guid.NewGuid().ToString("N");
        public ColonyIdentity(Game game) { }
        public override void ExposeData()
        {
            Scribe_Values.Look(ref ColonyId, "rimbotColonyId");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && string.IsNullOrEmpty(ColonyId))
                ColonyId = Guid.NewGuid().ToString("N");
        }
    }

}
