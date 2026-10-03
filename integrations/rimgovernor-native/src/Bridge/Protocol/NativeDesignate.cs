#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Generic Designate (#1350): one removal designation (Deconstruct, Mine,
    // CutPlant, Haul, RemoveFoundation) on a Ref target or a cell, placed
    // through the game's own designator and held by the named guard the
    // intent selects (NativeDesignationGuards). A designation already
    // standing (a player one is adopted) or a cell already cleared applies
    // again. A Deconstruct may swap a door for a wall (#1245); one under the
    // wall_upgrade guard resolves its site in NativeWallRemovalOperations,
    // and the acquisition guard's designations resolve in NativeAcquire.
    internal static class NativeDesignate
    {
        private const string Kind = "Designate";

        internal static bool Generic(Operations.DesignateIntent? intent) => intent != null && (intent.Target != null || intent.Cell != null);

        private sealed class Plan
        {
            internal Map Map = null!;
            internal Operations.ThingDesignation Designation;
            internal DesignationDef Def = null!;
            internal Thing? Target;
            internal IntVec3 Cell;
            internal string ExpectedDef = "";
            internal string? Guard;
            internal HashSet<IntVec3>? Ground;
            internal ThingDef? WallStuff;
            internal bool Present, Cleared;
        }

        private static DesignationDef? DefOf(Operations.ThingDesignation d)
        {
            switch (d)
            {
                case Operations.ThingDesignation.Deconstruct: return DesignationDefOf.Deconstruct;
                case Operations.ThingDesignation.Mine: return DesignationDefOf.Mine;
                case Operations.ThingDesignation.CutPlant: return DesignationDefOf.CutPlant;
                case Operations.ThingDesignation.Haul: return DesignationDefOf.Haul;
                case Operations.ThingDesignation.RemoveFoundation: return DesignationDefOf.RemoveFoundation;
                default: return null;
            }
        }
        // The guard each designation needs: building and rock removal are
        // never unguarded, the rest take none.
        private static string? GuardFor(Operations.ThingDesignation d) =>
            d == Operations.ThingDesignation.Deconstruct ? GuardNames.Enclosure : d == Operations.ThingDesignation.Mine ? GuardNames.MineSafety : null;

        // CutPlant on a harvestable tree is chop-wood (HarvestPlant): the
        // plain cut designator refuses such a tree outside the Orders menu.
        private static bool ChopWood(Plan p) => p.Designation == Operations.ThingDesignation.CutPlant
            && p.Target is Plant plant && plant.def.plant.IsTree && plant.HarvestableNow;
        private static DesignationDef PlacedDef(Plan p) => ChopWood(p) ? DesignationDefOf.HarvestPlant : p.Def;

        private static bool Standing(Plan p)
        {
            var manager = p.Map.designationManager;
            if (p.Designation == Operations.ThingDesignation.Mine) return manager.DesignationAt(p.Cell, DesignationDefOf.Mine) != null;
            if (p.Designation == Operations.ThingDesignation.RemoveFoundation) return manager.DesignationAt(p.Cell, DesignationDefOf.RemoveFoundation) != null;
            return p.Target != null && manager.DesignationOn(p.Target, PlacedDef(p)) != null;
        }
        private static bool OtherDesignation(Plan p)
        {
            if (p.Target == null || p.Target is Mineable) return false;
            var manager = p.Map.designationManager;
            return new[] { DesignationDefOf.CutPlant, DesignationDefOf.HarvestPlant, DesignationDefOf.Haul, DesignationDefOf.Deconstruct }
                .Any(d => d != PlacedDef(p) && manager.DesignationOn(p.Target, d) != null);
        }
        private static Designator Designator(Plan p)
        {
            switch (p.Designation)
            {
                case Operations.ThingDesignation.Mine: return new Designator_Mine();
                case Operations.ThingDesignation.CutPlant: return ChopWood(p) ? (Designator)new Designator_PlantsHarvestWood() : new Designator_PlantsCut { isOrder = true };
                case Operations.ThingDesignation.Haul: return new Designator_Haul();
                case Operations.ThingDesignation.RemoveFoundation: return new Designator_RemoveFoundation();
                default: return new Designator_Deconstruct();
            }
        }
        private static string? DesignatorRefusal(Plan p)
        {
            var report = p.Target != null && p.Designation != Operations.ThingDesignation.Mine ? Designator(p).CanDesignateThing(p.Target) : Designator(p).CanDesignateCell(p.Cell);
            if (report.Accepted) return null;
            return "the native designator refuses the target" + (string.IsNullOrEmpty(report.Reason) ? "" : ": " + report.Reason);
        }

        private static HashSet<IntVec3>? Ground(Operations.DesignateIntent intent, out string? refusal)
        {
            refusal = null;
            if (intent.ClearedGround.Count == 0) return null;
            var cells = new HashSet<IntVec3>();
            foreach (var r in intent.ClearedGround)
            {
                if (r?.Origin == null || !r.Origin.HasX || !r.Origin.HasZ || r.Origin.X < 0 || r.Origin.Z < 0 || r.Width <= 0 || r.Height <= 0 || r.Width > 4096 || r.Height > 4096)
                { refusal = "cleared ground rectangle is invalid"; return null; }
                for (var x = r.Origin.X; x < r.Origin.X + r.Width; x++)
                    for (var z = r.Origin.Z; z < r.Origin.Z + r.Height; z++) cells.Add(new IntVec3(x, 0, z));
            }
            return cells;
        }
        private static ThingDef? WallStuff(Thing target) => target is Building && target.def.IsDoor && target.Faction == Faction.OfPlayer && target.def.size == IntVec2.One
            && target.Stuff != null && GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Contains(target.Stuff) ? target.Stuff : null;
        private static bool ValidCell(Common.Cell? c) => c != null && c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0;

        // Resolve is the apply-time precondition list; null admits.
        private static string? Resolve(Operations.DesignateIntent intent, Common.ObservationContext context, out Plan plan, out Common.FailureCode code)
        {
            plan = new Plan(); code = Common.FailureCode.InvalidRequest;
            var def = intent.HasDesignation ? DefOf(intent.Designation) : null;
            if (def == null) return "the designation must be one of Deconstruct, Mine, CutPlant, Haul or RemoveFoundation";
            plan.Designation = intent.Designation; plan.Def = def;
            plan.Map = ProtoBoundary.LoadedMap(context);
            var map = plan.Map;
            // Guard: the one the designation needs, and only that one.
            plan.Guard = GuardFor(intent.Designation);
            var named = intent.HasGuard ? NativeDesignationGuards.Name(intent.Guard) : null;
            // A HAUL takes no guard, or the wastepack guard (#1683).
            if (intent.Designation == Operations.ThingDesignation.Haul && named == GuardNames.Wastepack) plan.Guard = named;
            if (named != plan.Guard) return plan.Guard == null ? "this designation takes no guard" : "this designation requires the " + plan.Guard + " guard";
            plan.Ground = Ground(intent, out var groundRefusal);
            if (groundRefusal != null) return groundRefusal;
            if (plan.Ground != null && plan.Guard != GuardNames.Enclosure) return "cleared ground belongs to the enclosure guard";
            if (intent.Cell != null && !ValidCell(intent.Cell)) return "the cell is invalid";
            if (intent.Target != null)
            {
                if (!intent.Target.HasId || !ProtoBoundary.IsIdentifier(intent.Target.Id)) return "the target reference is invalid";
                var found = RefIndex.Thing(map, intent.Target.Id);
                if (found == null || found.Destroyed || !found.Spawned) { code = Common.FailureCode.NotFound; return "the exact target is no longer spawned on this map"; }
                if (intent.Cell != null && (found.Position.x != intent.Cell.X || found.Position.z != intent.Cell.Z)) return "the target is not at the expected cell";
                if (found.Position.Fogged(map)) return "the target's cell is fogged";
                plan.Target = found; plan.Cell = found.Position;
                if (intent.Designation == Operations.ThingDesignation.Mine && !(found is Mineable)) return "Mine targets rock";
                if (intent.Designation == Operations.ThingDesignation.RemoveFoundation) return "RemoveFoundation names a cell, not a thing";
                plan.ExpectedDef = found.def.defName;
                if (intent.HasExpectedDef && intent.ExpectedDef != plan.ExpectedDef) return "the target is not the expected definition";
            }
            else
            {
                if (intent.Designation != Operations.ThingDesignation.Mine && intent.Designation != Operations.ThingDesignation.RemoveFoundation) return "this designation requires a target";
                if (!intent.HasExpectedDef || !ProtoBoundary.IsIdentifier(intent.ExpectedDef)) return "a cell designation requires the expected definition";
                plan.Cell = new IntVec3(intent.Cell!.X, 0, intent.Cell.Z); plan.ExpectedDef = intent.ExpectedDef;
                if (!plan.Cell.InBounds(map)) return "the cell is off the map";
                if (intent.Designation == Operations.ThingDesignation.Mine)
                {
                    var rock = ExcavationTools.RockAt(plan.Cell, map);
                    if (rock == null && plan.Cell.Walkable(map)) { plan.Cleared = true; return null; }
                    if (rock == null || rock.def.defName != intent.ExpectedDef) return "the expected rock is not at the cell";
                    plan.Target = rock;
                }
                else if (map.terrainGrid.FoundationAt(plan.Cell)?.defName != intent.ExpectedDef) { plan.Cleared = true; return null; }
            }
            if (intent.ReplaceWithWall)
            {
                if (intent.Designation != Operations.ThingDesignation.Deconstruct) return "only Deconstruct swaps a door for a wall";
                if (plan.Ground != null) return "a door-to-wall swap carries no cleared ground";
                plan.WallStuff = WallStuff(plan.Target!);
                if (plan.WallStuff == null) return "only a 1x1 player door of a wall stuff swaps for a wall";
            }
            if (plan.Target != null && plan.Target.IsForbidden(Faction.OfPlayer)) return "the target is forbidden";
            if (plan.Guard != null)
            {
                var blocker = NativeDesignationGuards.Registry.Admit(plan.Guard, new GuardSubject { Map = map, Cell = plan.Cell, Target = plan.Target, Ground = plan.Ground });
                if (blocker != null) return blocker;
            }
            plan.Present = Standing(plan);
            if (plan.Present) return null;
            if (OtherDesignation(plan)) return "the target carries another designation";
            if (plan.Designation == Operations.ThingDesignation.Haul
                && !StoreUtility.TryFindBestBetterStoreCellFor(plan.Target!, null, map, StoreUtility.CurrentStoragePriorityOf(plan.Target!), Faction.OfPlayer, out _))
                return "no stockpile accepts the thing";
            return DesignatorRefusal(plan);
        }

        private static bool WallUpgrade(Operations.DesignateIntent intent) => intent.HasGuard && intent.Guard == Operations.DesignationGuard.WallUpgrade;
        // The acquisition designations (#1046): HUNT, HARVEST_PLANT or MINE of
        // a census source, and their withdraw.
        private static bool Acquisition(Operations.DesignateIntent intent) => intent.HasGuard && intent.Guard == Operations.DesignationGuard.Acquisition;
        private static string? AcquisitionPairing(Operations.DesignateIntent intent) =>
            intent.Designation == Operations.ThingDesignation.Hunt || intent.Designation == Operations.ThingDesignation.HarvestPlant || intent.Designation == Operations.ThingDesignation.Mine
                ? null : "the acquisition guard holds HUNT, HARVEST_PLANT or MINE";
        private static Common.Failure? Pairing(Operations.DesignateIntent intent)
        {
            string? refusal = null;
            if (intent.Withdraw && !Acquisition(intent)) refusal = "only the acquisition guard withdraws";
            else if (Acquisition(intent)) refusal = AcquisitionPairing(intent);
            return refusal == null ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, ApplyPreconditions.Detail(Kind, refusal));
        }

        internal static Common.Failure? Validate(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var pairing = Pairing(intent);
            if (pairing != null) return pairing;
            if (WallUpgrade(intent)) return NativeWallRemovalOperations.Validate(intent, context);
            if (Acquisition(intent)) return NativeAcquire.Validate(intent, context);
            var refusal = Resolve(intent, context, out _, out var code);
            return refusal == null ? null : ProtoBoundary.Fail(code, ApplyPreconditions.Detail(Kind, refusal));
        }

        internal static Receipts.EffectEvidence Apply(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var pairing = Pairing(intent);
            if (pairing != null) throw new InvalidOperationException(pairing.Detail);
            if (WallUpgrade(intent)) return NativeWallRemovalOperations.Apply(intent, context);
            if (Acquisition(intent)) return NativeAcquire.Apply(intent, context);
            var refusal = Resolve(intent, context, out var plan, out _);
            if (refusal != null) throw new InvalidOperationException("Designate prerequisites changed before apply: " + refusal);
            if (plan.Cleared) return Evidence(plan, null, adopted: false);
            var adopted = plan.Present;
            var record = plan.Guard == null ? null : NativeDesignationGuards.Open(plan.Map, plan.Def, plan.Cell, plan.Target);
            if (record != null) return Evidence(plan, record, adopted);
            if (plan.Guard != null)
                record = new GuardedDesignation { Id = "designation-" + Guid.NewGuid().ToString("N"), Guard = plan.Guard, Designation = plan.Def.defName,
                    ExpectedDef = plan.ExpectedDef, MapId = plan.Map.uniqueID, X = plan.Cell.x, Z = plan.Cell.z,
                    ThingId = plan.Designation == Operations.ThingDesignation.Mine ? null : plan.Target?.GetUniqueLoadID(),
                    WallStuff = plan.WallStuff?.defName, Ground = plan.Ground?.ToList() };
            if (plan.WallStuff != null)
            {
                // A swap whose wall blueprint already stands applies again; a
                // swap the game lets the blueprint replace in place is one
                // build (the construct giver deconstructs the door, #1245).
                var standing = plan.Cell.GetThingList(plan.Map).FirstOrDefault(t => t is Blueprint_Build b && b.def.entityDefToBuild == ThingDefOf.Wall);
                if (standing != null) { record!.ReplacementId = standing.GetUniqueLoadID(); return Evidence(plan, record, adopted); }
                if (GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, plan.Cell, Rot4.North, plan.Map, false, null, null, plan.WallStuff).Accepted)
                { PlaceWall(record!, plan.Map, null, queued: false); return Evidence(plan, record, adopted); }
            }
            if (!plan.Present)
            {
                var designator = Designator(plan);
                if (plan.Designation == Operations.ThingDesignation.RemoveFoundation) plan.Map.designationManager.AddDesignation(new Designation(plan.Cell, DesignationDefOf.RemoveFoundation));
                else if (plan.Designation == Operations.ThingDesignation.Mine) designator.DesignateSingleCell(plan.Cell);
                else designator.DesignateThing(plan.Target);
            }
            // Vanilla removes a zero-work target (a sleeping spot) or any
            // target in god mode at once: that is the observed demolition.
            if (plan.Designation == Operations.ThingDesignation.Deconstruct && plan.Target!.Destroyed)
            {
                record!.Finished = Find.TickManager.TicksGame;
                if (record.WallStuff != null) PlaceWall(record, plan.Map, null, queued: false);
                return Evidence(plan, record, adopted);
            }
            if (!Standing(plan)) throw new InvalidOperationException("Native " + plan.Def.defName + " designation was not observed.");
            if (record != null) NativeDesignationGuards.Add(record);
            // Swap: the nearest capable builder takes the deconstruction now;
            // the wall follows it.
            if (plan.WallStuff != null) Order(plan.Target!, null, DeconstructGiver, queued: false);
            return Evidence(plan, record, adopted);
        }

        private static Receipts.EffectEvidence Evidence(Plan p, GuardedDesignation? record, bool adopted)
        {
            var cell = new Common.Cell { X = p.Cell.x, Z = p.Cell.z };
            if (p.Designation == Operations.ThingDesignation.Mine)
            {
                var now = ExcavationTools.RockAt(p.Cell, p.Map);
                var designated = now != null && ExcavationTools.Designated(p.Cell, p.Map);
                return new Receipts.EffectEvidence { Excavation = new Receipts.ExcavationEffect { Cell = cell, MineableDefName = p.ExpectedDef,
                    AdoptedExistingDesignation = adopted, Cleared = now == null, Designated = designated, Cancelled = now != null && !designated } };
            }
            if (p.Designation == Operations.ThingDesignation.Deconstruct)
            {
                var target = (Building)p.Target!;
                var effect = new Receipts.DeconstructEffect { TargetId = target.GetUniqueLoadID(), DesignationId = record?.Id ?? "",
                    DemolitionObserved = record != null && record.Finished >= 0,
                    WaitingForRoof = record != null && record.Finished < 0 && NativeDesignationGuards.RoofWait(target, p.Ground) != null };
                if (record?.ReplacementId != null) effect.ReplacementId = record.ReplacementId;
                return new Receipts.EffectEvidence { Deconstruct = effect };
            }
            var id = p.Target?.GetUniqueLoadID() ?? "foundation-" + p.Cell.x + "-" + p.Cell.z;
            return new Receipts.EffectEvidence { Designation = new Receipts.DesignationEffect { ThingId = id, DesignationDef = p.Def.defName,
                Present = p.Cleared || Standing(p), ResourceDef = p.ExpectedDef, Cell = cell } };
        }

        // ---- door-to-wall swap (#1245) ----
        // The wall blueprint goes on the door's cell under the game's own
        // placement rule; the builder is ordered to build it as player-forced
        // work. queued runs inside the builder's finishing deconstruct job.
        internal static void PlaceWall(GuardedDesignation record, Map map, Pawn? builder, bool queued)
        {
            var stuff = record.WallStuff == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(record.WallStuff);
            var cell = new IntVec3(record.X, 0, record.Z);
            if (stuff == null || !GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, cell, Rot4.North, map, false, null, null, stuff).Accepted) return;
            var wall = GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Wall, cell, map, Rot4.North, Faction.OfPlayer, stuff);
            record.ReplacementId = wall.GetUniqueLoadID();
            Order(wall, builder, BuildGiver, queued);
        }
        private static bool BuildGiver(WorkGiverDef def) => def.giverClass != null
            && (typeof(WorkGiver_ConstructDeliverResourcesToBlueprints).IsAssignableFrom(def.giverClass)
                || typeof(WorkGiver_ConstructFinishFrames).IsAssignableFrom(def.giverClass));
        private static bool DeconstructGiver(WorkGiverDef def) => def.giverClass != null && typeof(WorkGiver_Deconstruct).IsAssignableFrom(def.giverClass);
        private static IEnumerable<Pawn> Builders(Map map, IntVec3 cell) => map.mapPawns.FreeColonistsSpawned
            .Where(p => !p.Downed && !p.Drafted && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
                && !p.health.HasHediffsNeedingTend())
            .OrderBy(p => p.Position.DistanceToSquared(cell));
        private static void Order(Thing target, Pawn? preferred, Func<WorkGiverDef, bool> giver, bool queued)
        {
            foreach (var pawn in (preferred != null ? new[] { preferred } : Enumerable.Empty<Pawn>()).Concat(Builders(target.Map, target.Position)))
            {
                var result = WorkGiverDispatch.TryJob(pawn, target, giver, out _);
                if (result == null) continue;
                result.Job.playerForced = true;
                if (queued) { pawn.jobs.jobQueue.EnqueueFirst(result.Job, JobTag.Misc); return; }
                if (pawn.jobs.TryTakeOrderedJobPrioritizedWork(result.Job, result.Scanner, target.Position)) return;
            }
        }
    }
}
