using System.Collections.Generic;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // A setting of the exact unfinished thing, not a controller receipt or reservation.
    public sealed class ConstructionSkillState : GameComponent
    {
        public List<ConstructionSkillSetting> Settings = new List<ConstructionSkillSetting>();
        public ConstructionSkillState(Game game) { }
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
                Settings.RemoveAll(s => s.Target == null || s.Target.Destroyed || !(s.Target is Blueprint_Build || s.Target is Frame));
            Scribe_Collections.Look(ref Settings, "rimgovernorConstructionSkill", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (Settings == null) Settings = new List<ConstructionSkillSetting>();
                Settings.RemoveAll(s => s.Target == null || s.Target.Destroyed);
            }
        }
    }
    public sealed class ConstructionSkillSetting : IExposable
    {
        public Thing Target = null!;
        public int Minimum;
        public void ExposeData()
        {
            Scribe_References.Look(ref Target, "target");
            Scribe_Values.Look(ref Minimum, "minimum");
        }
    }
}
