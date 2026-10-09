using System.Collections.Generic;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Settings of the exact unfinished thing (finishing-skill floor and construction
    // tier), not a controller receipt or reservation. Each lives only while the
    // target is a blueprint or frame; completion drops it.
    public sealed class ConstructionSkillState : GameComponent
    {
        public List<ConstructionSkillSetting> Settings = new List<ConstructionSkillSetting>();
        public ConstructionSkillState(Game game) { }
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
                Settings.RemoveAll(s => s.Target == null || s.Target.Destroyed || !(s.Target is Blueprint_Build || s.Target is Frame));
            Scribe_Collections.Look(ref Settings, "rimgovernorConstructionTargets", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (Settings == null) Settings = new List<ConstructionSkillSetting>();
                Settings.RemoveAll(s => s.Target == null || s.Target.Destroyed);
            }
        }
    }
    public sealed class ConstructionSkillSetting : IExposable
    {
        public const int None = -1;
        public Thing Target = null!;
        // None means no finishing floor / no tier: the target is ungated on that axis.
        public int Minimum = None;
        public int Tier = None;
        public void ExposeData()
        {
            Scribe_References.Look(ref Target, "target");
            Scribe_Values.Look(ref Minimum, "minimum", None);
            Scribe_Values.Look(ref Tier, "tier", None);
        }
    }
}
