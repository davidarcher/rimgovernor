using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // Generic Designate: one removal designation (Deconstruct, Mine,
    // CutPlant, Haul, RemoveFoundation, or Cancel of a blueprint) on a Ref target or a cell, placed
    // through the game's own designator and held by the named guard the
    // intent selects (NativeDesignationGuards). A designation already
    // standing (a player one is adopted) or a cell already cleared applies
    // again. A Deconstruct may swap a door for a wall; one under the
    // wall_upgrade guard resolves its site in NativeWallRemovalOperations,
    // and the acquisition guard's designations resolve in NativeAcquire.
    // An unguarded Haul on an ordinary (alwaysHaulable) stack, which the
    // haul designator refuses (it takes only designateHaulable chunks),
    // stands no designation: it opens the stack's haul-out record, the
    // removal the construction read lists (#3625).
    internal static class NativeDesignate
    {
        private const string Kind = "Designate";

        internal static bool Generic(RimGovernor.Brain.GameOrders.Operations.DesignateIntent? intent) => intent != null && (intent.Target != null || intent.Location is RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At);

        private sealed class Plan
        {
            internal required Map Map;
            internal RimGovernor.Brain.GameOrders.Operations.ThingDesignation Designation;
            internal required DesignationDef Def;
            internal Thing? Target;
            internal IntVec3 Cell;
            internal string ExpectedDef = "";
            internal string? Guard;
            internal ThingDef? WallStuff;
            internal bool Present, Cleared, HaulOut;
        }

        private static DesignationDef? DefOf(RimGovernor.Brain.GameOrders.Operations.ThingDesignation d)
        {
            switch (d)
            {
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Deconstruct: return DesignationDefOf.Deconstruct;
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine: return DesignationDefOf.Mine;
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant: return DesignationDefOf.CutPlant;
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Haul: return DesignationDefOf.Haul;
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.RemoveFoundation: return DesignationDefOf.RemoveFoundation;
                default: return null;
            }
        }
        // The guard each designation needs: building and rock removal are
        // never unguarded, the rest take none.
        private static string? GuardFor(RimGovernor.Brain.GameOrders.Operations.ThingDesignation d) =>
            d == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Deconstruct ? GuardNames.Enclosure : d == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine ? GuardNames.MineSafety : null;

        // CutPlant on a harvestable tree is chop-wood (HarvestPlant): the
        // plain cut designator refuses such a tree outside the Orders menu.
        private static bool ChopWood(Plan p) => p.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant
            && p.Target is Plant plant && plant.def.plant.IsTree && plant.HarvestableNow;
        private static DesignationDef PlacedDef(Plan p) => ChopWood(p) ? DesignationDefOf.HarvestPlant : p.Def;

        private static bool Standing(Plan p)
        {
            if (p.HaulOut) return NativeDesignationGuards.HaulOut(p.Map, (p.Target ?? throw new System.InvalidOperationException("Required native value is unavailable: p.Target."))) != null;
            var manager = p.Map.designationManager;
            if (p.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine) return manager.DesignationAt(p.Cell, DesignationDefOf.Mine) != null;
            if (p.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.RemoveFoundation) return manager.DesignationAt(p.Cell, DesignationDefOf.RemoveFoundation) != null;
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
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine: return new Designator_Mine();
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant: return ChopWood(p) ? (Designator)new Designator_PlantsHarvestWood() : new Designator_PlantsCut { isOrder = true };
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Haul: return new Designator_Haul();
                case RimGovernor.Brain.GameOrders.Operations.ThingDesignation.RemoveFoundation: return new Designator_RemoveFoundation();
                default: return new Designator_Deconstruct();
            }
        }
        private static string? DesignatorRefusal(Plan p)
        {
            var report = p.Target != null && p.Designation != RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine ? Designator(p).CanDesignateThing(p.Target) : Designator(p).CanDesignateCell(p.Cell);
            if (report.Accepted) return null;
            return "the native designator refuses the target" + (string.IsNullOrEmpty(report.Reason) ? "" : ": " + report.Reason);
        }

        private static ThingDef? WallStuff(Thing target) => target is Building && target.def.IsDoor && target.Faction == Faction.OfPlayer && target.def.size == IntVec2.One
            && target.Stuff != null && GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Contains(target.Stuff) ? target.Stuff : null;
        private static bool ValidCell(RimGovernor.Brain.Domain.Cell c) => c.X >= 0 && c.Z >= 0;

        // Resolve is the apply-time precondition list; null admits.
        private static string? Resolve(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Plan? plan, out RimGovernor.Brain.GameFacts.Common.FailureCode code)
        {
            plan = null; code = RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest;
            if (!Enum.IsDefined(intent.Guard)) return "the designation guard must be a named state";
            var def = DefOf(intent.Designation);
            if (def == null) return "the designation must be one of Deconstruct, Mine, CutPlant, Haul, RemoveFoundation or Cancel";
            plan = new Plan { Designation = intent.Designation, Def = def, Map = GameThread.LoadedMap(context) };
            var map = plan.Map;
            // Guard: the one the designation needs, and only that one.
            plan.Guard = GuardFor(intent.Designation);
            var named = NativeDesignationGuards.Name(intent.Guard);
            // A HAUL takes no guard, or the wastepack guard.
            if (intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Haul && named == GuardNames.Wastepack) plan.Guard = named;
            if (named != plan.Guard) return plan.Guard == null ? "this designation takes no guard" : "this designation requires the " + plan.Guard + " guard";
            if (intent.Location is RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At at && !ValidCell(at.Cell)) return "the cell is invalid";
            if (intent.Location is not RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At and not RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget) return "the location must be a named state";
            if (intent.Target != null)
            {
                if (intent.Target.Id is null || !GameThread.IsIdentifier(intent.Target.Id)) return "the target reference is invalid";
                var found = RefIndex.Thing(map, intent.Target.Id);
                if (found == null || found.Destroyed || !found.Spawned) { code = RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound; return "the exact target is no longer spawned on this map"; }
                if (intent.Location is RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At expected && (found.Position.x != expected.Cell.X || found.Position.z != expected.Cell.Z)) return "the target is not at the expected cell";
                if (found.Position.Fogged(map)) return "the target's cell is fogged";
                plan.Target = found; plan.Cell = found.Position;
                if (intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine && !(found is Mineable)) return "Mine targets rock";
                if (intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.RemoveFoundation) return "RemoveFoundation names a cell, not a thing";
                plan.ExpectedDef = found.def.defName;
                if (intent.ExpectedDef is not null && (intent.ExpectedDef ?? "") != plan.ExpectedDef) return "the target is not the expected definition";
            }
            else
            {
                if (intent.Designation != RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine && intent.Designation != RimGovernor.Brain.GameOrders.Operations.ThingDesignation.RemoveFoundation) return "this designation requires a target";
                if (intent.ExpectedDef is null || !GameThread.IsIdentifier(intent.ExpectedDef)) return "a cell designation requires the expected definition";
                if (intent.Location is not RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At exact) return "a cell designation requires an exact cell";
                plan.Cell = new IntVec3(exact.Cell.X, 0, exact.Cell.Z); plan.ExpectedDef = intent.ExpectedDef;
                if (!plan.Cell.InBounds(map)) return "the cell is off the map";
                if (intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine)
                {
                    var rock = ExcavationTools.RockAt(plan.Cell, map);
                    if (rock == null && plan.Cell.Walkable(map)) { plan.Cleared = true; return null; }
                    if (rock == null || rock.def.defName != (intent.ExpectedDef ?? "")) return "the expected rock is not at the cell";
                    plan.Target = rock;
                }
                else if (map.terrainGrid.FoundationAt(plan.Cell)?.defName != (intent.ExpectedDef ?? "")) { plan.Cleared = true; return null; }
            }
            if (intent.ReplaceWithWall)
            {
                if (intent.Designation != RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Deconstruct) return "only Deconstruct swaps a door for a wall";
                plan.WallStuff = WallStuff((plan.Target ?? throw new System.InvalidOperationException("Required native value is unavailable: plan.Target.")));
                if (plan.WallStuff == null) return "only a 1x1 player door of a wall stuff swaps for a wall";
            }
            if (plan.Target != null && plan.Target.IsForbidden(Faction.OfPlayer)) return "the target is forbidden";
            plan.HaulOut = plan.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Haul && plan.Guard == null && NativeDesignationGuards.HaulOutStack((plan.Target ?? throw new System.InvalidOperationException("Required native value is unavailable: plan.Target.")));
            if (plan.Guard != null)
            {
                var blocker = NativeDesignationGuards.Registry.Admit(plan.Guard, new GuardSubject { Map = map, Cell = plan.Cell, Target = plan.Target });
                if (blocker != null) return blocker;
            }
            plan.Present = Standing(plan);
            if (plan.Present) return null;
            if (OtherDesignation(plan)) return "the target carries another designation";
            if (plan.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Haul
                && !StoreUtility.TryFindBestBetterStoreCellFor(plan.Target, null, map, StoreUtility.CurrentStoragePriorityOf(plan.Target), Faction.OfPlayer, out _))
                return "no stockpile accepts the thing";
            return plan.HaulOut ? null : DesignatorRefusal(plan);
        }

        private static bool WallUpgrade(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent) => intent.Guard == RimGovernor.Brain.GameOrders.Operations.DesignationGuard.WallUpgrade;
        // The acquisition designations: HUNT, HARVEST_PLANT or MINE of
        // a census source, and their withdraw.
        private static bool Acquisition(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent) => intent.Guard == RimGovernor.Brain.GameOrders.Operations.DesignationGuard.Acquisition;
        private static string? AcquisitionPairing(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent) =>
            intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Hunt || intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.HarvestPlant || intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine
                ? null : "the acquisition guard holds HUNT, HARVEST_PLANT or MINE";
        private static RimGovernor.Brain.GameFacts.Common.Failure? Pairing(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent)
        {
            string? refusal = null;
            if (intent.Withdraw && !Acquisition(intent)) refusal = "only the acquisition guard withdraws";
            else if (Acquisition(intent)) refusal = AcquisitionPairing(intent);
            return refusal == null ? null : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, ApplyPreconditions.Detail(Kind, refusal));
        }

        // ---- blueprint cancel ----
        // CANCEL destroys one exact player blueprint through the game's
        // cancel designator (DestroyMode.Cancel). It takes no guard; a
        // blueprint already gone applies again, and the evidence is the
        // blueprint gone. A frame is deconstructed, not cancelled.
        private static bool Cancel(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent) => intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Cancel;
        private const string CancelDef = "Cancel";

        private static string? ResolveCancel(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Thing? blueprint, out string id, out RimGovernor.Brain.GameFacts.Common.FailureCode code)
        {
            blueprint = null; id = ""; code = RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest;
            if (intent.Guard != RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard) return "this designation takes no guard";
            if (intent.Target?.Id is null || !GameThread.IsIdentifier(intent.Target.Id)) return "CANCEL requires a blueprint target";
            if (intent.Location is RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At at && !ValidCell(at.Cell)) return "the cell is invalid";
            if (intent.Location is not RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At and not RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget) return "the location must be a named state";
            if (intent.ReplaceWithWall) return "only Deconstruct swaps a door for a wall";
            id = intent.Target.Id;
            var map = GameThread.LoadedMap(context);
            var found = RefIndex.Thing(map, id);
            if (found == null || found.Destroyed || !found.Spawned) return null;
            if (intent.Location is RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At expected && (found.Position.x != expected.Cell.X || found.Position.z != expected.Cell.Z)) return "the target is not at the expected cell";
            if (!(found is Blueprint) || found.Faction != Faction.OfPlayer) return "CANCEL targets a player blueprint";
            if (found.Position.Fogged(map)) return "the target's cell is fogged";
            if (intent.ExpectedDef is not null && intent.ExpectedDef != found.def.defName) return "the target is not the expected definition";
            var report = new Designator_Cancel().CanDesignateThing(found);
            if (!report.Accepted) return "the native designator refuses the target" + (string.IsNullOrEmpty(report.Reason) ? "" : ": " + report.Reason);
            blueprint = found;
            return null;
        }

        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence CancelEvidence(string id) =>
            new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.DesignationEffect { ThingId = id, DesignationDef = CancelDef, Present = true } };

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var pairing = Pairing(intent);
            if (pairing != null) return pairing;
            if (WallUpgrade(intent)) return NativeWallRemovalOperations.Validate(intent, context);
            if (Acquisition(intent)) return NativeAcquire.Validate(intent, context);
            if (Cancel(intent))
            {
                var cancel = ResolveCancel(intent, context, out _, out _, out var cancelCode);
                return cancel == null ? null : GameThread.Fail(cancelCode, ApplyPreconditions.Detail(Kind, cancel));
            }
            var refusal = Resolve(intent, context, out _, out var code);
            return refusal == null ? null : GameThread.Fail(code, ApplyPreconditions.Detail(Kind, refusal));
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var pairing = Pairing(intent);
            if (pairing != null) throw new InvalidOperationException(pairing.Detail);
            if (WallUpgrade(intent)) return NativeWallRemovalOperations.Apply(intent, context);
            if (Acquisition(intent)) return NativeAcquire.Apply(intent, context);
            if (Cancel(intent))
            {
                var cancel = ResolveCancel(intent, context, out var blueprint, out var id, out _);
                if (cancel != null) throw new InvalidOperationException("Designate prerequisites changed before apply: " + cancel);
                if (blueprint != null) new Designator_Cancel().DesignateThing(blueprint);
                if (blueprint != null && !blueprint.Destroyed) throw new InvalidOperationException("Native blueprint cancel was not observed.");
                return CancelEvidence(id);
            }
            var refusal = Resolve(intent, context, out var plan, out _);
            if (refusal != null || plan == null) throw new InvalidOperationException("Designate prerequisites changed before apply: " + refusal);
            if (plan.Cleared) return Evidence(plan, null);
            if (plan.HaulOut)
            {
                if (!plan.Present) NativeDesignationGuards.StandHaulOut(plan.Map, (plan.Target ?? throw new System.InvalidOperationException("Required native value is unavailable: plan.Target.")));
                return Evidence(plan, null);
            }
            var record = plan.Guard == null ? null : NativeDesignationGuards.Open(plan.Map, plan.Def, plan.Cell, plan.Target);
            if (record != null) return Evidence(plan, record);
            if (plan.Guard != null)
                record = new GuardedDesignation { Id = "designation-" + Guid.NewGuid().ToString("N"), Guard = plan.Guard, Designation = plan.Def.defName,
                    ExpectedDef = plan.ExpectedDef, MapId = plan.Map.uniqueID, X = plan.Cell.x, Z = plan.Cell.z,
                    ThingId = plan.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine ? null : plan.Target?.GetUniqueLoadID(),
                    WallStuff = plan.WallStuff?.defName };
            if (plan.WallStuff != null)
            {
                // A swap whose wall blueprint already stands applies again; a
                // swap the game lets the blueprint replace in place is one
                // build (the construct giver deconstructs the door).
                var standing = plan.Cell.GetThingList(plan.Map).FirstOrDefault(t => t is Blueprint_Build b && b.def.entityDefToBuild == ThingDefOf.Wall);
                if (standing != null) { (record ?? throw new System.InvalidOperationException("Required native value is unavailable: record.")).ReplacementId = standing.GetUniqueLoadID(); return Evidence(plan, record); }
                if (GenConstruct.CanPlaceBlueprintAt(ThingDefOf.Wall, plan.Cell, Rot4.North, plan.Map, false, null, null, plan.WallStuff).Accepted)
                { PlaceWall((record ?? throw new System.InvalidOperationException("Required native value is unavailable: record.")), plan.Map, null, queued: false); return Evidence(plan, record); }
            }
            if (!plan.Present)
            {
                var designator = Designator(plan);
                if (plan.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.RemoveFoundation) plan.Map.designationManager.AddDesignation(new Designation(plan.Cell, DesignationDefOf.RemoveFoundation));
                else if (plan.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine) designator.DesignateSingleCell(plan.Cell);
                else designator.DesignateThing(plan.Target);
            }
            // Vanilla removes a zero-work target (a sleeping spot) or any
            // target in god mode at once: that is the observed demolition.
            if (plan.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Deconstruct && (plan.Target ?? throw new System.InvalidOperationException("Required native value is unavailable: plan.Target.")).Destroyed)
            {
                (record ?? throw new System.InvalidOperationException("Required native value is unavailable: record.")).Finished = Find.TickManager.TicksGame;
                if (record.WallStuff != null) PlaceWall(record, plan.Map, null, queued: false);
                return Evidence(plan, record);
            }
            if (!Standing(plan)) throw new InvalidOperationException("Native " + plan.Def.defName + " designation was not observed.");
            if (record != null) NativeDesignationGuards.Add(record);
            // Swap: the nearest capable builder takes the deconstruction now;
            // the wall follows it.
            if (plan.WallStuff != null) Order((plan.Target ?? throw new System.InvalidOperationException("Required native value is unavailable: plan.Target.")), null, DeconstructGiver, queued: false);
            return Evidence(plan, record);
        }

        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence(Plan p, GuardedDesignation? record)
        {
            if (p.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine)
            {
                var now = ExcavationTools.RockAt(p.Cell, p.Map);
                var designated = now != null && ExcavationTools.Designated(p.Cell, p.Map);
                return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.ExcavationCase() };
            }
            if (p.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Deconstruct)
            {
                var target = (Building)(p.Target ?? throw new System.InvalidOperationException("Required native value is unavailable: p.Target."));
                var effect = new RimGovernor.Brain.GameFacts.Receipts.DeconstructEffect { TargetId = target.GetUniqueLoadID(), DesignationId = record?.Id ?? "" };
                if (record?.ReplacementId != null) effect = effect with { ReplacementId = record.ReplacementId };
                return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = effect };
            }
            var id = p.Target?.GetUniqueLoadID() ?? "foundation-" + p.Cell.x + "-" + p.Cell.z;
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.DesignationEffect { ThingId = id, DesignationDef = p.Def.defName,
                Present = p.Cleared || Standing(p) } };
        }

        // ---- door-to-wall swap ----
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
