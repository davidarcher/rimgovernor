#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeDeconstructionRecord
    {
        internal readonly Building Target;
        internal readonly Map Map;
        internal readonly IntVec3 Cell;
        internal readonly Rot4 Rotation;
        internal readonly string Id = "deconstruct-" + Guid.NewGuid().ToString("N");
        internal readonly string Before;
        internal Designation? Designation;
        internal bool Complete, Released;
        internal readonly HashSet<string> Workers = new HashSet<string>();
        internal NativeDeconstructionRecord(Building target, Common.ObservationContext context)
        { Target = target; Map = target.Map; Cell = target.Position; Rotation = target.Rotation; Before = NativeBuildingObservationTools.Token(target, context).Token; }

        // After explicit admission, never adopt a replacement designation on
        // the same target. Reference identity is scoped to the loaded game.
        internal bool Owns => !Complete && !Released && Designation != null &&
            ReferenceEquals(Map.designationManager.DesignationOn(Target, DesignationDefOf.Deconstruct), Designation);
        internal string? Blocker()
        {
            if (Released) return "Controller deconstruction was released.";
            if (!Target.Spawned || Target.Map != Map || Target.Position != Cell || Target.Rotation != Rotation)
                return "Exact deconstruction occupant changed without an observed demolition.";
            if (!Owns) return "Controller designation was removed or replaced; player ownership is preserved.";
            return NativeDeconstructionOperations.Safety(Target);
        }
        internal Receipts.EffectEvidence Evidence(Common.ObservationContext context)
        {
            var effect = new Receipts.DeconstructEffect { TargetId = Target.GetUniqueLoadID(), DesignationId = Id,
                DemolitionObserved = Complete, Site = new Receipts.SnapshotEvidence { EntityId = Target.GetUniqueLoadID(), BeforeToken = Before } };
            effect.WorkerIds.AddRange(Workers.OrderBy(id => id, StringComparer.Ordinal));
            if (Target.Spawned && Target.Map == Map) effect.Site.AfterToken = NativeBuildingObservationTools.Token(Target, context).Token;
            return new Receipts.EffectEvidence { Deconstruct = effect };
        }
    }

    internal static class NativeDeconstructionOperations
    {
        private static bool installed;
        private static Game? game;
        private static readonly List<NativeDeconstructionRecord> records = new List<NativeDeconstructionRecord>();
        private static void CurrentRecords()
        { if (!ReferenceEquals(game, Current.Game)) { records.Clear(); game = Current.Game; } }
        private static NativeDeconstructionRecord? Claim(Thing target)
        { CurrentRecords(); return records.LastOrDefault(r => ReferenceEquals(r.Target, target) && r.Owns); }
        private static void Install()
        {
            if (installed) return;
            var harmony = new Harmony("rimgovernor.deconstruction");
            harmony.Patch(AccessTools.Method(typeof(JobDriver_Deconstruct), "FinishedRemoving"),
                prefix: new HarmonyMethod(typeof(NativeDeconstructionOperations), nameof(BeforeRemoval)),
                finalizer: new HarmonyMethod(typeof(NativeDeconstructionOperations), nameof(AfterRemoval)));
            harmony.Patch(AccessTools.Method(typeof(WorkGiver_Deconstruct), nameof(WorkGiver_Deconstruct.HasJobOnThing)),
                postfix: new HarmonyMethod(typeof(NativeDeconstructionOperations), nameof(Eligible)));
            harmony.Patch(AccessTools.Method(typeof(JobDriver_Deconstruct), "MakeNewToils"),
                postfix: new HarmonyMethod(typeof(NativeDeconstructionOperations), nameof(GuardJob)));
            NativeControlAuthority.GenerationChanged += (authority, snapshot, previous) =>
            {
                if (!snapshot.Active && ReferenceEquals(game, Current.Game))
                    using (authority.Owned()) ReleaseAll();
            };
            installed = true;
        }
        private static void Eligible(Thing t, ref bool __result)
        { var record = Claim(t); if (record != null && (!Supervisor.IsActive || record.Blocker() != null)) __result = false; }
        private static void GuardJob(JobDriver_Deconstruct __instance)
        {
            var record = Claim(__instance.job.targetA.Thing);
            if (record == null) return;
            // Releasing the designation must not detach an already-issued job
            // from its guard. The retained record observes release or replacement.
            __instance.FailOn(() => !Supervisor.IsActive || record.Blocker() != null);
        }
        private static bool BeforeRemoval(JobDriver_Deconstruct __instance, out NativeDeconstructionRecord? __state)
        {
            __state = Claim(__instance.job.targetA.Thing);
            if (__state == null) return true;
            if (!Supervisor.IsActive || __state.Blocker() != null) { __state = null; return false; }
            __state.Workers.Add(__instance.pawn.GetUniqueLoadID());
            return true;
        }
        private static Exception? AfterRemoval(NativeDeconstructionRecord? __state, Exception? __exception)
        {
            // Disappearance alone is never completion: this callback brackets
            // the native deconstruction job's FinishedRemoving method.
            if (__state != null && __exception == null && __state.Target.Destroyed) __state.Complete = true;
            return __exception;
        }

        internal static string? Safety(Building target)
        {
            if (!target.Spawned || !target.DeconstructibleBy(Faction.OfPlayer))
                return "Target must be a spawned building deconstructible by the player.";
            if (target.OccupiedRect().Cells.Any(c => !c.InBounds(target.Map) || c.Fogged(target.Map))) return "Unknown target geometry.";
            if (target.IsForbidden(Faction.OfPlayer) || target.IsBurning()) return "Target is forbidden or burning.";
            // Colony enclosure demolition must use RemoveWall's replacement
            // guards; generic deconstruction cannot bypass them. A wall with
            // an enclosed room on every open side only joins rooms (a suite's
            // old wall once its grown ring stands, #1218), so it is no
            // enclosure.
            if (target.Faction == Faction.OfPlayer && target.def == ThingDefOf.Wall)
            {
                var rooms = GenAdj.CardinalDirections.Select(d => target.Position + d)
                    .Where(c => c.InBounds(target.Map)).Select(c => c.GetRoom(target.Map)).OfType<Room>().ToList();
                bool Enclosed(Room r) => r.ProperRoom && !r.TouchesMapEdge;
                if (rooms.Any(Enclosed) && !rooms.All(Enclosed))
                    return "Enclosing colony walls require guarded RemoveWall.";
            }
            if (!target.def.holdsRoof) return null;
            var shrineStructure = NativeShrineBreachSafety.StructuralCells(target);
            if (shrineStructure != null)
                return RoofSupportSafety.Blocker(target, null, out _, shrineStructure);
            var cells = target.OccupiedRect().Cells.ToList();
            if (cells.Any(c => !RoofSupportSafety.GeometryKnown(target.Map, c))) return "Unknown roof support geometry.";
            return ExcavationSafety.Check(target.Map, cells, out _, out var blocker) == ExcavationSafety.Support.Supported ? null : blocker ?? "Roof support is unproven.";
        }
        // The apply-time precondition list for Deconstruct: the exact target,
        // its safety, no pending wall upgrade, and the game designator. A
        // target this controller already owns applies again with its record.
        private static string? Refusal(Operations.DeconstructIntent? intent, Common.ObservationContext context, out Building? target, out Common.FailureCode code)
        {
            target = null; code = Common.FailureCode.InvalidRequest;
            if (intent == null || !intent.HasTargetId || !ProtoBoundary.IsIdentifier(intent.TargetId)) return "Deconstruct requires an exact target.";
            var map = ProtoBoundary.ResolveMap(context);
            target = RefIndex.Thing<Building>(map, intent.TargetId);
            if (target == null) { code = Common.FailureCode.NotFound; return "Exact deconstruction target is absent."; }
            var blocker = Safety(target);
            if (blocker != null) return blocker;
            if (Claim(target) != null) return null;
            if (WallUpgradeSafety.Pending(target) != null) return "A pending wall upgrade owns the target.";
            if (map!.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) == null && !new Designator_Deconstruct().CanDesignateThing(target).Accepted)
                return "Native deconstruction designator refused the target.";
            return null;
        }
        internal static Common.Failure? Validate(Operations.DeconstructIntent? intent, Common.ObservationContext context)
        {
            CurrentRecords();
            var refusal = Refusal(intent, context, out _, out var code);
            return refusal == null ? null : ProtoBoundary.Fail(code, refusal);
        }
        // Apply designates the target (adopting a player designation already
        // on it) and records it so the job guards hold the work to authority
        // and safety; revoking authority releases every owned designation.
        internal static Receipts.EffectEvidence Apply(Operations.DeconstructIntent intent, Common.ObservationContext context)
        {
            Install(); CurrentRecords();
            var refusal = Refusal(intent, context, out var target, out _);
            if (refusal != null) throw new InvalidOperationException("Deconstruction prerequisites changed before apply: " + refusal);
            var owned = Claim(target!);
            if (owned != null) return owned.Evidence(context);
            var record = new NativeDeconstructionRecord(target!, context);
            if (record.Map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) == null)
                new Designator_Deconstruct().DesignateThing(target);
            record.Designation = record.Map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct);
            if (record.Designation == null) throw new InvalidOperationException("Native designation was not created.");
            records.Add(record);
            return record.Evidence(context);
        }
        internal static int ReleaseAll()
        {
            CurrentRecords(); var count = 0;
            foreach (var record in records.Where(r => !r.Complete && !r.Released))
            {
                if (record.Owns) { record.Map.designationManager.RemoveDesignation(record.Designation); count++; }
                record.Released = true;
            }
            return count;
        }
    }

    internal sealed class DeconstructActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeDeconstructionOperations.Validate(action.Deconstruct, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeDeconstructionOperations.Apply(action.Deconstruct, context);
    }
}
