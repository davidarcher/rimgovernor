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

        // Setting lookup by target is on the work-giver scan path (every candidate frame, per
        // pawn, per giver), so it is a dictionary rebuilt lazily after any change to Settings
        // or to a setting's Target (Invalidate), never a list scan.
        private Dictionary<Thing, ConstructionSkillSetting>? index;
        public void Invalidate() => index = null;
        public ConstructionSkillSetting? Find(Thing t)
        {
            if (index == null)
            {
                index = new Dictionary<Thing, ConstructionSkillSetting>();
                foreach (var setting in Settings)
                    if (setting.Target != null && !index.ContainsKey(setting.Target)) index[setting.Target] = setting;
            }
            return index.TryGetValue(t, out var found) ? found : null;
        }
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
