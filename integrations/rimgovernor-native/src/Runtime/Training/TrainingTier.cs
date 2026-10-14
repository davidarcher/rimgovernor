#nullable disable
using Verse;

namespace RimGovernor.Runtime
{
    // One rung of the shooting practice ladder (#2681), read from the
    // practice weapon's def (Defs/ThingDefs/TrainingRange.xml). The brain mirrors the
    // table in Brain.Policy/TrainingTier.cs.
    public sealed class TrainingTier : DefModExtension
    {
        // Rung number, 0 (practice bow) to 3.
        public int tier;
        // XP multiplier against the tier-0 bow.
        public float multiplier = 1f;
        // XP applied per shot.
        public int xpPerShot;
        // Skill level at which this weapon stops teaching.
        public int skillCeiling;
        // Research project that unlocks the tier; empty means none. A name, not a
        // reference, so a tier gated on an expansion project loads without it.
        public string gateResearch = "";
    }
}
