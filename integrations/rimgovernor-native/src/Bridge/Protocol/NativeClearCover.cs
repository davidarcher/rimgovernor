using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // Raider-cover census: the kind of thing whose fill gives a cell
    // its cover and whether a removal designation already stands on it. The
    // clearance itself is a Designate on the thing (NativeDesignate).
    internal static class NativeClearCover
    {
        internal const string Mine = "Mine", CutPlant = "CutPlant", Haul = "Haul", Deconstruct = "Deconstruct";

        // The kind of a cover thing; null for one the designators cannot remove (a corpse, filth).
        internal static RimGovernor.Brain.GameFacts.Observations.CoverKind? KindOf(Thing thing)
        {
            if (thing is Plant) return RimGovernor.Brain.GameFacts.Observations.CoverKind.Plant;
            if (thing is Mineable) return RimGovernor.Brain.GameFacts.Observations.CoverKind.Mineable;
            if (thing.def.category == ThingCategory.Item && thing.def.IsWithinCategory(ThingCategoryDefOf.Chunks)) return RimGovernor.Brain.GameFacts.Observations.CoverKind.Chunk;
            if (thing is Building) return RimGovernor.Brain.GameFacts.Observations.CoverKind.Building;
            return null;
        }
        internal static string DesignationFor(Thing thing)
        {
            switch (KindOf(thing)) {
                case RimGovernor.Brain.GameFacts.Observations.CoverKind.Plant: return CutPlant;
                case RimGovernor.Brain.GameFacts.Observations.CoverKind.Mineable: return Mine;
                case RimGovernor.Brain.GameFacts.Observations.CoverKind.Chunk: return Haul;
                case RimGovernor.Brain.GameFacts.Observations.CoverKind.Building: return Deconstruct;
                default: return "";
            }
        }
        internal static bool Designated(Thing thing)
        {
            var manager = thing.Map.designationManager;
            if (thing is Mineable) return manager.DesignationAt(thing.Position, DesignationDefOf.Mine) != null;
            return manager.DesignationOn(thing, DesignationDefOf.CutPlant) != null || manager.DesignationOn(thing, DesignationDefOf.HarvestPlant) != null
                || manager.DesignationOn(thing, DesignationDefOf.Haul) != null || manager.DesignationOn(thing, DesignationDefOf.Deconstruct) != null;
        }
    }
}
