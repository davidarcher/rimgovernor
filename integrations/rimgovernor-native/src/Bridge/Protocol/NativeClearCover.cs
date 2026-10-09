#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Raider-cover census: the kind of thing whose fill gives a cell
    // its cover and whether a removal designation already stands on it. The
    // clearance itself is a Designate on the thing (NativeDesignate).
    internal static class NativeClearCover
    {
        internal const string Mine = "Mine", CutPlant = "CutPlant", Haul = "Haul", Deconstruct = "Deconstruct";

        internal static Obs.CoverKind KindOf(Thing thing)
        {
            if (thing is Plant) return Obs.CoverKind.Plant;
            if (thing is Mineable) return Obs.CoverKind.Mineable;
            if (thing.def.category == ThingCategory.Item && thing.def.IsWithinCategory(ThingCategoryDefOf.Chunks)) return Obs.CoverKind.Chunk;
            if (thing is Building) return Obs.CoverKind.Building;
            return Obs.CoverKind.Unspecified;
        }
        internal static string DesignationFor(Thing thing)
        {
            switch (KindOf(thing)) {
                case Obs.CoverKind.Plant: return CutPlant;
                case Obs.CoverKind.Mineable: return Mine;
                case Obs.CoverKind.Chunk: return Haul;
                case Obs.CoverKind.Building: return Deconstruct;
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
