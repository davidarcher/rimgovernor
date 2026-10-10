#nullable disable
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimGovernor.Runtime
{
    // One rung of the melee practice ladder (#2703), read from the practice
    // weapon's def (Defs/ThingDefs/SparringRing.xml). Damage is flat per damage
    // type and vanilla pays the melee XP, so a tier buys only a higher ceiling.
    // Go mirrors the table in go/internal/policy/sparring_tier.go and a Go test
    // pins every value.
    public sealed class SparringTier : DefModExtension
    {
        // Rung number, 0 (padded club) to 3.
        public int tier;
        // Skill level at which this weapon stops teaching.
        public int skillCeiling;
        // Research project that unlocks the tier; empty means none. A name, not a
        // reference, so a project the game lacks never unlocks (as Go's census).
        public string gateResearch = "";
        // Practice apparel (#2706) the spar job issues with the weapon, by def
        // name: the set a tier's swap puts on (the swap itself is #2709).
        public List<string> apparel = new List<string>();
    }

    // Melee mirror of RangeTraining's tier selection (UnlockedSparringTier in
    // go/internal/policy/sparring_tier.go).
    public static class SparringRules
    {
        private static List<ThingDef> tierWeapons;

        private static List<ThingDef> TierWeapons =>
            tierWeapons ??= DefDatabase<ThingDef>.AllDefs.Where(d => d.GetModExtension<SparringTier>() != null)
                .OrderBy(d => d.GetModExtension<SparringTier>().tier).ToList();

        private static bool Unlocked(SparringTier tier)
        {
            if (string.IsNullOrEmpty(tier.gateResearch)) return true;
            var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(tier.gateResearch);
            return project != null && project.IsFinished;
        }

        // The best tier whose gate research is finished; tier 0 has no gate, so
        // this is null only when no sparring weapon is loaded.
        public static ThingDef UnlockedWeapon()
        {
            ThingDef best = null;
            foreach (var weapon in TierWeapons)
            {
                if (Unlocked(weapon.GetModExtension<SparringTier>())) best = weapon;
            }
            return best;
        }

        public static int Ceiling => UnlockedWeapon()?.GetModExtension<SparringTier>()?.skillCeiling ?? 0;
    }
}
