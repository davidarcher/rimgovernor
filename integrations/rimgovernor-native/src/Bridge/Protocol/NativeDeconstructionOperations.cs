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

        // Never adopt another designation, including a player replacement on
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
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            if (ProtoBoundary.ResolveMap(context) != Map)
            { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Deconstruction map changed." }; return result; }
            var evidence = Evidence(context);
            if (Complete) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else if (Blocker() is string blocker) result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Detail = blocker, Evidence = evidence };
            else result.Pending = new Receipts.PendingEffect { Evidence = evidence };
            return result;
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
            installed = true;
        }
        private static void Eligible(Thing t, ref bool __result)
        { var record = Claim(t); if (record != null && (!Supervisor.IsActive || record.Blocker() != null)) __result = false; }
        private static void GuardJob(JobDriver_Deconstruct __instance)
        {
            if (Claim(__instance.job.targetA.Thing) == null) return;
            __instance.FailOn(() => { var record = Claim(__instance.job.targetA.Thing); return record != null && (!Supervisor.IsActive || record.Blocker() != null); });
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
            if (!target.Spawned || target.Faction == Faction.OfPlayer || !target.DeconstructibleBy(Faction.OfPlayer))
                return "Target must be a spawned non-colony building deconstructible by the player.";
            if (target.OccupiedRect().Cells.Any(c => !c.InBounds(target.Map) || c.Fogged(target.Map))) return "Unknown target geometry.";
            if (target.IsForbidden(Faction.OfPlayer) || target.IsBurning()) return "Target is forbidden or burning.";
            if (!target.def.holdsRoof) return null;
            var cells = target.OccupiedRect().Cells.ToList();
            if (cells.Any(c => !RoofSupportSafety.GeometryKnown(target.Map, c))) return "Unknown roof support geometry.";
            return ExcavationSafety.Check(target.Map, cells, out _, out var blocker) == ExcavationSafety.Support.Supported ? null : blocker ?? "Roof support is unproven.";
        }
        private static bool Prepare(Operations.Deconstruct command, Common.ObservationContext context, out Building? target, out Common.Failure failure)
        {
            target = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Deconstruct requires an exact target and no snapshot token.");
            if (command?.Target == null || !command.Target.HasEntityId || !ProtoBoundary.IsIdentifier(command.Target.EntityId) || command.Target.HasExpectedSnapshotToken) return false;
            var map = ProtoBoundary.ResolveMap(context);
            target = map?.listerThings.AllThings.OfType<Building>().FirstOrDefault(b => b.GetUniqueLoadID() == command.Target.EntityId);
            if (target == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact deconstruction target is absent."); return false; }
            var blocker = Safety(target);
            if (blocker != null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, blocker); return false; }
            if (map!.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) != null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Existing designation is preserved; observe its original receipt if controller-owned."); return false; }
            if (!new Designator_Deconstruct().CanDesignateThing(target).Accepted)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native deconstruction designator refused the target."); return false; }
            return true;
        }
        internal static Operations.PreviewReply Preview(Operations.Deconstruct command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition;
            try
            {
                Install(); CurrentRecords();
                if (!Prepare(request.Operation.Deconstruct, context, out var target, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                var record = new NativeDeconstructionRecord(target!, context);
                state.Deconstructions.Add(pre.Attempt.Clone(), record); records.Add(record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(request.Operation.Deconstruct, context, out var current, out _) || !ReferenceEquals(current, target))
                        throw new InvalidOperationException("Deconstruction occupant or safety changed before apply.");
                    new Designator_Deconstruct().DesignateThing(target);
                    record.Designation = record.Map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct);
                    if (record.Designation == null) throw new InvalidOperationException("Native designation was not created.");
                    evidence = record.Evidence(context);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Deconstruction interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Deconstruction failed: " + error.GetType().Name) };
            }
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
        internal static Operations.ExecuteReply ExecuteRelease(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            var pre = request.Precondition;
            try
            {
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                int count;
                using (authority.Owned()) count = ReleaseAll();
                var evidence = new Receipts.EffectEvidence { ReleaseDeconstructions = new Receipts.ReleaseDeconstructionsEffect { ReleasedCount = count } };
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, null!, "Deconstruction release interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Deconstruction release failed: " + error.GetType().Name) };
            }
        }
    }
}
