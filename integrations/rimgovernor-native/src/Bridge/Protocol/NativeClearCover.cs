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
    // Raider-cover clearance (#581): the defense site census names the thing
    // whose fill gives a cell its cover, and CoverIntent (Actions/Apply, #940)
    // places the one designation the game's own designators would use to
    // remove it (Mine on a mineable, CutPlant on a plant, Haul on a chunk,
    // Deconstruct on a building). The designation is the whole write and an
    // existing one applies again; ordinary work removes the thing and the
    // next census sees it gone. Roof support and deconstruction safety are
    // the same rules excavation and deconstruction apply.
    internal static class NativeClearCover
    {
        internal const string Kind = "Clear cover";
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
        private static DesignationDef Def(string designation)
        {
            switch (designation) {
                case Mine: return DesignationDefOf.Mine;
                case CutPlant: return DesignationDefOf.CutPlant;
                case Haul: return DesignationDefOf.Haul;
                default: return DesignationDefOf.Deconstruct;
            }
        }
        internal static bool Designated(Thing thing)
        {
            var manager = thing.Map.designationManager;
            if (thing is Mineable) return manager.DesignationAt(thing.Position, DesignationDefOf.Mine) != null;
            return manager.DesignationOn(thing, DesignationDefOf.CutPlant) != null || manager.DesignationOn(thing, DesignationDefOf.HarvestPlant) != null
                || manager.DesignationOn(thing, DesignationDefOf.Haul) != null || manager.DesignationOn(thing, DesignationDefOf.Deconstruct) != null;
        }
        // CutPlant on a harvestable tree is the chop-wood designation
        // (HarvestPlant): the plain cut designator refuses such a tree
        // outside the Orders menu, and chopping keeps the wood.
        private static bool ChopWood(Thing thing, string designation) => designation == CutPlant
            && thing is Plant plant && plant.def.plant.IsTree && plant.HarvestableNow;
        private static bool Present(Thing thing, string designation) => designation == Mine
            ? thing.Map.designationManager.DesignationAt(thing.Position, DesignationDefOf.Mine) != null
            : thing.Map.designationManager.DesignationOn(thing, ChopWood(thing, designation) ? DesignationDefOf.HarvestPlant : Def(designation)) != null;

        private static Thing? Find(Map map, string id) => RefIndex.Thing(map, id);

        private static bool Worker(Pawn p, Thing thing, WorkTypeDef work) => p.workSettings?.Initialized == true
            && p.workSettings.GetPriority(work) > 0 && !p.WorkTypeIsDisabled(work)
            && !p.Downed && !p.Drafted && !p.InMentalState && !thing.IsForbidden(p)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(thing, PathEndMode.Touch, Danger.None);
        private static WorkTypeDef WorkFor(string designation)
        {
            switch (designation) {
                case Mine: return WorkTypeDefOf.Mining;
                case CutPlant: return WorkTypeDefOf.PlantCutting;
                case Haul: return WorkTypeDefOf.Hauling;
                default: return WorkTypeDefOf.Construction;
            }
        }
        private static Designator DesignatorFor(string designation, Thing thing)
        {
            switch (designation) {
                case Mine: return new Designator_Mine();
                case CutPlant: return ChopWood(thing, designation) ? (Designator)new Designator_PlantsHarvestWood() : new Designator_PlantsCut { isOrder = true };
                case Haul: return new Designator_Haul();
                default: return new Designator_Deconstruct();
            }
        }
        // DesignatorRefusal is the game designator's own verdict, with its
        // reason when it gives one, so a refused clearance says why.
        private static string? DesignatorRefusal(string designation, Thing thing)
        {
            var report = DesignatorFor(designation, thing).CanDesignateThing(thing);
            if (report.Accepted) return null;
            return "The native designator refuses the thing" + (string.IsNullOrEmpty(report.Reason) ? "." : ": " + report.Reason);
        }
        // Safety is the kind's own guard beyond the designator: excavation
        // geometry for rock, deconstruction safety for a building, a store
        // that will take a chunk (a Haul designation nobody can fulfil pends
        // forever).
        private static string? Safety(Thing thing, string designation, Map map)
        {
            switch (designation) {
                case Mine: return BridgeCommon.Try(() => ResourceAcquisitionTools.MiningBlocker(thing, map), "Unknown excavation geometry");
                case Deconstruct: return NativeDeconstructionOperations.Safety((Building)thing);
                case Haul: return StoreUtility.TryFindBestBetterStoreCellFor(thing, null, map, StoreUtility.CurrentStoragePriorityOf(thing), Faction.OfPlayer, out _) ? null : "No stockpile accepts the chunk.";
                default: return null;
            }
        }

        private static ApplyPreconditions Rules(Operations.CoverIntent? intent, Map map, out Thing? thing, out string? blocker)
        {
            Thing? found = null; string? refused = null;
            var designation = intent?.DesignationDef ?? "";
            var rules = new ApplyPreconditions(Kind)
                .Require(() => Valid(intent), "clearance requires a cover thing, its cell and one of Mine, CutPlant, Haul or Deconstruct")
                .Present(() => (found = Find(map, intent!.ThingId)) != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact cover thing is no longer spawned on this map")
                .Require(() => found!.Position.x == intent!.Cell.X && found.Position.z == intent.Cell.Z, "the cover thing is not at the expected cell")
                .Require(() => !found!.Position.Fogged(map), "the cover cell is fogged")
                .Require(() => DesignationFor(found!) == designation, "the designation does not match the cover thing's kind")
                .Require(() => found!.def.fillPercent > 0, "the thing gives no cover")
                .Require(() => !found!.IsForbidden(Faction.OfPlayer), "the cover thing is forbidden")
                .Require(() => Present(found!, designation) || !Designated(found!), "the cover thing carries another designation")
                .Require(() => Present(found!, designation) || (refused = Safety(found!, designation, map)) == null, "the kind's safety rule refuses the thing")
                .Require(() => Present(found!, designation) || (refused = DesignatorRefusal(designation, found!)) == null, "the native designator refuses the thing")
                .Require(() => Present(found!, designation) || map.mapPawns.FreeColonistsSpawned.Any(p => Worker(p, found!, WorkFor(designation))), "no free colonist with the work type enabled can reach the thing");
            thing = found; blocker = refused;
            return rules;
        }

        private static bool Valid(Operations.CoverIntent? intent) => intent != null
            && intent.HasThingId && ProtoBoundary.IsIdentifier(intent.ThingId) && intent.HasDesignationDef && intent.Cell != null && intent.Cell.HasX && intent.Cell.HasZ
            && (intent.DesignationDef == Mine || intent.DesignationDef == CutPlant || intent.DesignationDef == Haul || intent.DesignationDef == Deconstruct);

        internal static Common.Failure? Validate(Operations.CoverIntent? intent, Common.ObservationContext context)
        {
            var rules = Rules(intent, ProtoBoundary.LoadedMap(context), out _, out var blocker);
            if (rules.Holds) return null;
            return blocker != null ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, Kind + ": " + blocker) : rules.Failure();
        }

        internal static Receipts.EffectEvidence Apply(Operations.CoverIntent intent, Common.ObservationContext context)
        {
            var rules = Rules(intent, ProtoBoundary.LoadedMap(context), out var thing, out var blocker);
            if (!rules.Holds) throw new InvalidOperationException("Clear cover prerequisites changed before apply: " + (blocker ?? rules.Reason));
            if (!Present(thing!, intent.DesignationDef)) DesignatorFor(intent.DesignationDef, thing!).DesignateThing(thing);
            var evidence = new Receipts.EffectEvidence { Designation = new Receipts.DesignationEffect { ThingId = thing!.GetUniqueLoadID(), DesignationDef = intent.DesignationDef,
                Present = Present(thing, intent.DesignationDef), ResourceDef = thing.def.defName, Cell = new Common.Cell { X = thing.Position.x, Z = thing.Position.z } } };
            if (!evidence.Designation.Present) throw new InvalidOperationException("Native clearance designation was not observed.");
            return evidence;
        }
    }

    internal sealed class CoverActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeClearCover.Validate(action.Cover, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeClearCover.Apply(action.Cover, context);
    }
}
