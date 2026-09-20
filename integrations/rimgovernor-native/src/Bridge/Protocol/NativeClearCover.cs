#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    // whose fill gives a cell its cover, and ClearCover places the one
    // designation the game's own designators would use to remove it (Mine on
    // a mineable, CutPlant on a plant, Haul on a chunk, Deconstruct on a
    // building). The designation is the whole write; ordinary work removes
    // the thing, and the receipt observes it gone (completed), still
    // designated (pending) or standing undesignated (unsuccessful). Roof
    // support and deconstruction safety are the same rules excavation and
    // deconstruction apply.
    internal static class NativeClearCover
    {
        internal const string Kind = "Clear cover";
        internal const string Mine = "Mine", CutPlant = "CutPlant", Haul = "Haul", Deconstruct = "Deconstruct";

        internal static bool Valid(Operations.ClearCover? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Target) && command.HasDesignationDef && command.Cell != null && command.Cell.HasX && command.Cell.HasZ
            && (command.DesignationDef == Mine || command.DesignationDef == CutPlant || command.DesignationDef == Haul || command.DesignationDef == Deconstruct);

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

        internal static string Token(Common.Identity identity, Thing thing)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(thing.GetUniqueLoadID()); writer.Write(thing.def.defName); writer.Write(thing.Position.x); writer.Write(thing.Position.z); writer.Write(Designated(thing));
                }
                using (var hash = SHA256.Create())
                    return "cover-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        private static Thing? Find(Map map, string id) => map.listerThings.AllThings.FirstOrDefault(t => t.Spawned && t.GetUniqueLoadID() == id);

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

        private static bool Prepare(Operations.ClearCover command, Common.ObservationContext context, out Thing? thing, out Common.Failure failure)
        {
            thing = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "ClearCover requires an exact cover thing snapshot and one of Mine, CutPlant, Haul or Deconstruct.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var found = Find(map, command.Target.EntityId);
            var designation = command.DesignationDef;
            string? blocker = null;
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact cover thing is no longer spawned on this map")
                .Require(() => found!.Position.x == command.Cell.X && found.Position.z == command.Cell.Z, "the cover thing is not at the expected cell")
                .Require(() => !found!.Position.Fogged(map), "the cover cell is fogged")
                .Require(() => DesignationFor(found!) == designation, "the designation does not match the cover thing's kind")
                .Require(() => found!.def.fillPercent > 0, "the thing gives no cover")
                .Require(() => !found!.IsForbidden(Faction.OfPlayer), "the cover thing is forbidden")
                .Require(() => !Designated(found!), "the cover thing is already designated")
                .Require(() => (blocker = Safety(found!, designation, map)) == null, "the kind's safety rule refuses the thing")
                .Require(() => (blocker = DesignatorRefusal(designation, found!)) == null, "the native designator refuses the thing")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Worker(p, found!, WorkFor(designation))), "no free colonist with the work type enabled can reach the thing")
                .Token(() => Token(context.Identity, found!) == command.Target.ExpectedSnapshotToken, "the cover snapshot changed since it was read");
            if (!rules.Holds) { failure = blocker != null ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, Kind + ": " + blocker) : rules.Failure(); return false; }
            thing = found;
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Thing thing, string designation) => new Receipts.EffectEvidence {
            Designation = new Receipts.DesignationEffect { ThingId = thing.GetUniqueLoadID(), DesignationDef = designation,
                Present = Present(thing, designation), ResourceDef = thing.def.defName, Cell = new Common.Cell { X = thing.Position.x, Z = thing.Position.z } } };

        internal static Operations.PreviewReply Preview(Operations.ClearCover command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out var thing, out var failure)) return new Operations.PreviewReply { Failure = failure };
                var proposed = Evidence(thing!, command.DesignationDef); proposed.Designation.Present = true;
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true, Projected = proposed } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Clear cover preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition;
            var command = request.Operation.ClearCover;
            try
            {
                if (!Prepare(command, context, out var thing, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success || !Prepare(command, context, out var checkedThing, out failure) || !ReferenceEquals(checkedThing, thing))
                        throw new InvalidOperationException("Clear cover admission changed before effect.");
                    DesignatorFor(command.DesignationDef, thing!).DesignateThing(thing);
                    evidence = Evidence(thing!, command.DesignationDef);
                    if (!evidence.Designation.Present) throw new InvalidOperationException("Native clearance designation was not observed.");
                    state.CoverClearances.Add(pre.Attempt.Clone(), evidence.Designation.Clone());
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Clear cover admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted ClearCover requires observation: " + error.GetType().Name) };
            }
        }

        // Observe: the thing gone from its cell is the designation's ordinary
        // outcome and completes the attempt (a hauled chunk elsewhere on the
        // map counts as gone); standing with its designation is pending;
        // standing without it is unsuccessful, never re-designated here.
        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Receipts.DesignationEffect original)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            try
            {
                var map = ProtoBoundary.LoadedMap(context);
                var thing = Find(map, original.ThingId);
                if (thing == null || thing.Destroyed || !thing.Spawned || thing.Position.x != original.Cell.X || thing.Position.z != original.Cell.Z)
                {
                    result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Designation = original.Clone() } };
                    return result;
                }
                var evidence = Evidence(thing, original.DesignationDef);
                if (evidence.Designation.Present) result.Pending = new Receipts.PendingEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "The cover thing stands without its clearance designation; a cancelled designation is not repeated." };
                return result;
            }
            catch (Exception)
            {
                result.CompleteInspection = false;
                result.Unknown = new Receipts.UnknownEffect { Reason = "The exact cover thing could not be inspected; absence does not prove the clearance." };
                return result;
            }
        }
    }
}
