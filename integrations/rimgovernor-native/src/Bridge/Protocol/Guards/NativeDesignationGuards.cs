#nullable enable
using RimGovernor.Host.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    // What a guard judges: the designation's map cell, its thing (the
    // building, or the rock at a Mine cell), plus the wall-upgrade site.
    internal sealed class GuardSubject
    {
        internal Map Map = null!;
        internal IntVec3 Cell;
        internal Thing? Target;
        internal WallRemovalRecord? Wall;
    }

    // The game's named tick guards and the hooks that hold guarded
    // designations to them. Admission runs Registry.Admit; the deconstruct
    // and mine job hooks re-check every open GuardState record before the
    // work lands: a failed check drops the designation and ends the job, a
    // wait (roof collapse, see RoofWait) holds pawns off it. Revoking
    // authority releases every open guarded designation.
    internal static class NativeDesignationGuards
    {
        internal static readonly GuardRegistry<GuardSubject> Registry = new GuardRegistry<GuardSubject>()
            .Register(GuardNames.Enclosure, s => s.Target is Building b ? Enclosure(b) :"The enclosure guard holds a building.",
                s => s.Target is Building b ? RoofWait(b) : null)
            .Register(GuardNames.MineSafety, MineSafety, s => MineSafetyRule.Wait(CollapsePending(s.Map)))
            .Register(GuardNames.Acquisition, s => s.Target is Mineable rock ? ResourceAcquisitionTools.MiningBlocker(rock, s.Map) : null,
                s => MineSafetyRule.Wait(ResourceAcquisitionTools.CollapsePending(s.Map)))
            .Register(GuardNames.WallUpgrade, s => s.Wall == null ? "The wall_upgrade guard holds a wall-upgrade site." : WallUpgradeSafety.Check(s.Wall))
            .Register(GuardNames.Wastepack, Wastepack);

        // ---- wastepack ----
        // A thing with the game's CompDissolution that is neither frozen nor
        // inside an atomizer: hauling it to storage is what stops it
        // deteriorating into pollution.
        private static string? Wastepack(GuardSubject s)
        {
            var dissolution = (s.Target as ThingWithComps)?.GetComp<CompDissolution>();
            if (dissolution == null || !s.Target!.Spawned) return "The wastepack guard holds a spawned thing with a dissolution comp.";
            if (dissolution.InAtomizer) return "The wastepack is already in an atomizer.";
            return dissolution.IsFrozen ? "The wastepack is already frozen." : null;
        }

        internal static string? Name(Operations.DesignationGuard guard)
        {
            switch (guard)
            {
                case Operations.DesignationGuard.Enclosure: return GuardNames.Enclosure;
                case Operations.DesignationGuard.MineSafety: return GuardNames.MineSafety;
                case Operations.DesignationGuard.WallUpgrade: return GuardNames.WallUpgrade;
                case Operations.DesignationGuard.Acquisition: return GuardNames.Acquisition;
                case Operations.DesignationGuard.Wastepack: return GuardNames.Wastepack;
                default: return null;
            }
        }

        // ---- mine_safety ----
        private static bool CollapsePending(Map map) => map.roofCollapseBuffer.CellsMarkedToCollapse.Count > 0;
        private static string? MineSafety(GuardSubject s) => MineSafetyRule.Check(CollapsePending(s.Map), () => ExcavationTools.CellBlocker(s.Cell, s.Map),
            () => ExcavationSafety.Check(s.Map, new[] { s.Cell }, out _, out var support, throughFog: true) == ExcavationSafety.Support.Supported ? null : support ?? "Roof support is unproven.");

        // ---- enclosure ----
        private static string? Enclosure(Building target) =>
            Safety(target) ?? (WallUpgradeSafety.Pending(target) != null ? "A pending wall upgrade owns the target." : null);

        // The roof-collapse guard, held at the work giver (Eligible) with a
        // completion-time backstop (BeforeRemoval): the designation stands
        // and pawns wait, losing no progress (the removal job resets its work
        // on every start), while taking the building down would collapse a
        // roof. A roof-holder waits until its support is proven (clearance removes
        // the roof first).
        internal static string? RoofWait(Building target)
        {
            if (!target.Spawned) return null;
            if (!target.def.holdsRoof) return null;
            var shrineStructure = NativeShrineBreachSafety.StructuralCells(target);
            if (shrineStructure != null)
                return RoofSupportSafety.Blocker(target, null, out _, shrineStructure);
            var cells = target.OccupiedRect().Cells.ToList();
            if (cells.Any(c => !RoofSupportSafety.GeometryKnown(target.Map, c))) return "Unknown roof support geometry.";
            return ExcavationSafety.Check(target.Map, cells, out _, out var blocker) == ExcavationSafety.Support.Supported ? null : blocker ?? "Roof support is unproven.";
        }
        // The permanent blockers only; roof collapse is RoofWait's transient hold.
        private static string? Safety(Building target)
        {
            if (!target.Spawned || !target.DeconstructibleBy(Faction.OfPlayer))
                return "Target must be a spawned building deconstructible by the player.";
            if (target.OccupiedRect().Cells.Any(c => !c.InBounds(target.Map) || c.Fogged(target.Map))) return "Unknown target geometry.";
            if (target.IsForbidden(Faction.OfPlayer) || target.IsBurning()) return "Target is forbidden or burning.";
            return null;
        }

        // ---- ledger ----
        internal static GuardState State()
        {
            var state = Current.Game.GetComponent<GuardState>();
            if (state == null) { state = new GuardState(Current.Game); Current.Game.components.Add(state); }
            return state;
        }
        private static Map? MapOf(GuardedDesignation r) => Find.Maps.FirstOrDefault(m => m.uniqueID == r.MapId);
        private static IntVec3 CellOf(GuardedDesignation r) => new IntVec3(r.X, 0, r.Z);

        internal static GuardedDesignation? Open(Map map, DesignationDef def, IntVec3 cell, Thing? thing)
        {
            if (Current.Game == null) return null;
            var id = thing != null && def != DesignationDefOf.Mine ? thing.GetUniqueLoadID() : null;
            return State().Records.LastOrDefault(r => r.Open && r.MapId == map.uniqueID && r.Designation == def.defName
                && (id != null ? r.ThingId == id : r.X == cell.x && r.Z == cell.z));
        }

        internal static GuardSubject? Subject(GuardedDesignation r)
        {
            var map = MapOf(r);
            if (map == null) return null;
            var cell = CellOf(r);
            Thing? target = r.Designation == DesignationDefOf.Mine.defName ? ExcavationTools.RockAt(cell, map) : r.ThingId != null ? RefIndex.Thing(map, r.ThingId) : null;
            return new GuardSubject { Map = map, Cell = cell, Target = target, Wall = r.Wall };
        }

        // Hold is the in-progress re-check: true holds the job off the work.
        // A failed guard drops the designation; the record keeps the blocker.
        internal static bool Hold(GuardedDesignation r) => Verdict(r) != GuardVerdict.Proceed;

        private static GuardVerdict Verdict(GuardedDesignation r)
        {
            if (!r.Open || !Supervisor.IsActive) return GuardVerdict.Wait;
            var subject = Subject(r);
            if (subject == null) { r.Cancelled = true; return GuardVerdict.Cancel; }
            if (r.Designation == DesignationDefOf.Deconstruct.defName && (subject.Target == null || subject.Target.Position != subject.Cell))
            { Cancel(r, subject.Map, "Exact deconstruction occupant changed without an observed demolition."); return GuardVerdict.Cancel; }
            var verdict = Registry.Recheck(r.Guard, subject, out var blocker);
            if (verdict == GuardVerdict.Cancel) Cancel(r, subject.Map, blocker);
            return verdict;
        }

        private static void Cancel(GuardedDesignation r, Map map, string? blocker)
        {
            r.Blocker = blocker; r.Cancelled = true;
            ModLog.Warn("guards", "" + r.Guard + " guard cancelled " + r.Designation + " at (" + r.X + "," + r.Z + ") on map " + r.MapId + ": " + blocker);
            var designation = Designation(r, map);
            if (designation != null) map.designationManager.RemoveDesignation(designation);
        }

        private static Designation? Designation(GuardedDesignation r, Map map)
        {
            if (r.Designation == DesignationDefOf.Mine.defName) return map.designationManager.DesignationAt(CellOf(r), DesignationDefOf.Mine);
            var thing = r.ThingId != null ? RefIndex.Thing(map, r.ThingId) : null;
            var def = DefDatabase<DesignationDef>.GetNamedSilentFail(r.Designation);
            return thing == null || def == null ? null : map.designationManager.DesignationOn(thing, def);
        }

        internal static GuardedDesignation Add(GuardedDesignation record)
        {
            Install();
            State().Records.Add(record);
            return record;
        }

        internal static int ReleaseAll()
        {
            if (Current.Game == null) return 0;
            var count = 0;
            foreach (var r in State().Records.Where(r => r.Open).ToList())
            {
                var map = MapOf(r);
                var designation = map == null ? null : Designation(r, map);
                r.Cancelled = true;
                if (designation == null) continue;
                map!.designationManager.RemoveDesignation(designation); count++;
                ModLog.Warn("guards", "" + r.Guard + " guard released " + r.Designation + " at (" + r.X + "," + r.Z + ") on map " + r.MapId + ": control authority ended");
            }
            return count;
        }

        // ---- job hooks ----
        private static bool installed;
        internal static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.designation-guards");
            harmony.Patch(AccessTools.Method(typeof(JobDriver_Deconstruct), "FinishedRemoving"),
                prefix: new HarmonyMethod(typeof(NativeDesignationGuards), nameof(BeforeRemoval)),
                finalizer: new HarmonyMethod(typeof(NativeDesignationGuards), nameof(AfterRemoval)));
            harmony.Patch(AccessTools.Method(typeof(WorkGiver_Deconstruct), nameof(WorkGiver_Deconstruct.HasJobOnThing)),
                postfix: new HarmonyMethod(typeof(NativeDesignationGuards), nameof(Eligible)));
            harmony.Patch(AccessTools.Method(typeof(JobDriver_Deconstruct), "MakeNewToils"),
                postfix: new HarmonyMethod(typeof(NativeDesignationGuards), nameof(GuardJob)));
            var mine = AccessTools.Method(typeof(JobDriver_Mine), "DoDamage") ?? throw new InvalidOperationException("Native mining damage contract unavailable");
            harmony.Patch(mine, prefix: new HarmonyMethod(typeof(NativeDesignationGuards), nameof(BeforePick)),
                postfix: new HarmonyMethod(typeof(NativeDesignationGuards), nameof(AfterPick)));
            harmony.Patch(AccessTools.Method(typeof(DesignationManager), "RemoveDesignation"),
                postfix: new HarmonyMethod(typeof(NativeDesignationGuards), nameof(Removed)));
            NativeControlAuthority.GenerationChanged += (authority, snapshot, previous) =>
            {
                // Revocation has already ended the owned scope, so the
                // release runs unscoped.
                if (!snapshot.Active) ReleaseAll();
            };
            installed = true;
        }

        private static GuardedDesignation? Deconstruction(Thing? t) =>
            t?.Map == null || Current.Game == null ? null : Open(t.Map, DesignationDefOf.Deconstruct, t.Position, t);

        private static void Removed(DesignationManager __instance, Designation des)
        {
            if (Current.Game == null) return;
            var r = des.target.HasThing ? Open(__instance.map, des.def, des.target.Thing.Position, des.target.Thing)
                : Open(__instance.map, des.def, des.target.Cell, null);
            if (r != null) r.Cancelled = true;
        }
        private static void Eligible(Thing t, ref bool __result)
        { var r = Deconstruction(t); if (r != null && Hold(r)) __result = false; }
        private static void GuardJob(JobDriver_Deconstruct __instance)
        {
            var r = Deconstruction(__instance.job.targetA.Thing);
            // The retained record observes release or replacement.
            if (r != null) __instance.FailOn(() => Hold(r));
        }
        private static bool BeforeRemoval(JobDriver_Deconstruct __instance, out GuardedDesignation? __state)
        {
            __state = Deconstruction(__instance.job.targetA.Thing);
            if (__state == null) return true;
            if (Hold(__state)) { __state = null; return false; }
            return true;
        }
        private static Exception? AfterRemoval(JobDriver_Deconstruct __instance, GuardedDesignation? __state, Exception? __exception)
        {
            // Disappearance alone is never completion: this brackets the
            // native deconstruction job's FinishedRemoving method.
            if (__state != null && __exception == null && __instance.job.targetA.Thing is Thing target && target.Destroyed)
            {
                __state.Finished = Find.TickManager.TicksGame; __state.Cancelled = false;
                if (__state.WallStuff != null)
                    try
                    {
                        if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                            throw new InvalidOperationException("Native authority is unavailable.");
                        using (authority.Owned()) NativeDesignate.PlaceWall(__state, target.Map ?? Find.Maps.First(m => m.uniqueID == __state.MapId), __instance.pawn, queued: true);
                    }
                    catch (Exception e) { ModLog.Warn("guards", "door-to-wall swap: " + e.Message); }
            }
            return __exception;
        }
        private static bool BeforePick(JobDriver_Mine __instance, Thing target, out GuardedDesignation? __state)
        {
            __state = target?.Map == null || Current.Game == null ? null : Open(target.Map, DesignationDefOf.Mine, target.Position, null);
            if (__state == null) return true;
            // A wait holds the pick and keeps the designation and the job; only
            // a cancel (designation dropped) ends the job.
            var verdict = Verdict(__state);
            if (verdict == GuardVerdict.Proceed) return true;
            __state = null;
            if (verdict == GuardVerdict.Cancel) __instance.EndJobWith(JobCondition.Incompletable);
            return false;
        }
        private static void AfterPick(Thing target, GuardedDesignation? __state)
        {
            if (__state == null || !target.Destroyed) return;
            __state.Finished = Find.TickManager.TicksGame; __state.Cancelled = false;
            // An acquisition mine that opens protected colony space is walled.
            if (__state.Guard == GuardNames.Acquisition && MapOf(__state) is Map map) ResourceAcquisitionTools.ReplaceWall(CellOf(__state), map);
        }
    }
}
