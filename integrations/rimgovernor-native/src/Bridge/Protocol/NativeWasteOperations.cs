#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for MaintainWaste's exact-item haul/burial order. Reuses
    // the same real native hauling WorkGiver scan the legacy JSON
    // home/manage_waste tool (HomeWasteTools.Haul) issues, so an order here
    // is exactly the job a player's float-menu click would produce. The
    // waste target's CAS token is self-computed and self-checked the same
    // way NativeRecoveryOperations does for buildings: no per-item lookup RPC
    // exposes it (WasteItem.Snapshot is declared in observations.proto but
    // ReadWaste has no native implementation), so a caller obtains the token
    // via bridge.ReadWasteTarget's rimgovernor/observations_get_cells read
    // with Things requested (NativeObservationTools.CellThingRow computes the
    // identical Token(...) hash for the item's cell), not the legacy
    // home/waste_state JSON tool.
    internal sealed class NativeWasteRecord
    {
        private readonly NativeControlIdentity identity;
        private readonly Pawn pawn;
        private readonly Thing target;
        private readonly Job job;
        private readonly int jobId;
        private readonly string trackingId;
        private readonly Common.ObservationContext admitted;
        internal NativeWasteRecord(NativeControlIdentity identity, Pawn pawn, Thing target, Job job, string trackingId, Common.ObservationContext context)
        { this.identity = identity; this.pawn = pawn; this.target = target; this.job = job; jobId = job.loadID; this.trackingId = trackingId; admitted = context.Clone(); }

        // Issued describes only whether THIS call just issued a new job; Progress
        // always reports Issued=false, matching the haul/recovery evidence contract.
        internal Receipts.EffectEvidence Evidence(NativePawnSnapshot snapshot, bool issued, bool verified) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = snapshot.PawnId, JobId = jobId, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = target.GetUniqueLoadID() },
                Issued = issued, Verified = verified,
                VerifiedReason = verified ? "Exact issued native waste job and quantity ledger observed." : "Issued job outcome requires observation.",
                Drafted = false, ResultingSnapshotToken = snapshot.Token,
            }
        };

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick
                    || NativePawnControlState.Observe(identity, pawn, out var snapshot) != NativePawnControlResult.Ready || snapshot == null)
                    throw new InvalidOperationException("Current waste pawn context cannot be inspected.");
                var record = HaulTracking.Lookup(trackingId);
                if (record == null)
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact waste haul tracking record is no longer observable; absence does not prove delivery." };
                }
                else if (record.Complete)
                {
                    result.CompleteInspection = true;
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(snapshot, false, true) };
                }
                else if (record.Blocker != null)
                {
                    result.CompleteInspection = true;
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect
                    { Reason = Receipts.UnsuccessfulReason.Interrupted, Evidence = Evidence(snapshot, false, false), Detail = record.Blocker };
                }
                else
                {
                    result.CompleteInspection = true;
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(snapshot, false, true) };
                }
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Waste inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeWasteOperations
    {
        internal static bool Valid(Operations.ManageWaste? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Target) && NativeDraftProtocol.ValidEntity(command.Pawn)
            && command.Pawn.EntityId != command.Target.EntityId && command.UnwantedIds.Count <= 256 && command.BuryIds.Count <= 256;

        private static HashSet<string> Set(IEnumerable<string> ids) => new HashSet<string>(ids, StringComparer.Ordinal);

        // Ports HomeWasteTools.Protection.
        private static string? Protection(Thing thing, bool burialAllowed)
        {
            if (!thing.Spawned || thing.Position.Fogged(thing.Map)) return "held_or_unobserved";
            if (thing.IsForbidden(Faction.OfPlayer)) return "player_forbidden";
            if (thing.questTags != null && thing.questTags.Count > 0) return "quest_item";
            if (thing.def.comps != null && thing.def.comps.Any(c => c is CompProperties_Dissolution
                || c is CompProperties_GasOnDamage || c is CompProperties_Explosive)) return "hazardous_item_requires_specialized_containment";
            if (thing is MinifiedThing || thing is Pawn || thing is Building) return "protected_possession";
            var corpse = thing as Corpse;
            if (corpse != null)
            {
                var inner = corpse.InnerPawn;
                if (inner == null) return "corpse_identity_unknown";
                if (!burialAllowed && (inner.Faction == Faction.OfPlayer || inner.Name != null)) return "named_or_colony_corpse";
                if (!burialAllowed && inner.RaceProps.Humanlike) return "human_corpse_requires_funeral_policy";
            }
            return null;
        }

        // Ports HomeWasteTools.Kind.
        private static string? Kind(Thing thing, HashSet<string> unwanted)
        {
            var rot = thing.TryGetComp<CompRottable>();
            if (thing is Corpse && rot != null && rot.Stage != RotStage.Fresh) return "corpse";
            if (!(thing is Corpse) && rot != null && rot.Stage != RotStage.Fresh) return "spoiled";
            return unwanted.Contains(thing.GetUniqueLoadID()) ? "unwanted" : null;
        }

        // Ports HomeWasteTools.DirtyCell: a conservative separation contract,
        // not a claim that any outdoor dump is harmless.
        private static bool DirtyCell(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map) || cell.Fogged(map) || cell.Roofed(map)
                || map.areaManager.Home[cell] || cell.GetRoom(map)?.UsesOutdoorTemperature != true) return false;
            return !map.listerBuildings.allBuildingsColonist.Any(b => b.Position.DistanceToSquared(cell) < 144);
        }

        private static bool Stored(Thing thing)
        {
            var zone = thing.Position.GetZone(thing.Map) as Zone_Stockpile;
            return zone != null && zone.GetStoreSettings().AllowedToAccept(thing) && DirtyCell(thing.Map, thing.Position);
        }

        // Self-computed, self-checked CAS token; see the class remarks.
        internal static string Token(Common.Identity identity, Thing thing)
        {
            var rot = thing.TryGetComp<CompRottable>();
            var corpse = thing as Corpse;
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(thing.GetUniqueLoadID()); writer.Write(thing.def.defName);
                    writer.Write(thing.Position.x); writer.Write(thing.Position.z); writer.Write(thing.stackCount);
                    writer.Write(thing.IsForbidden(Faction.OfPlayer)); writer.Write(rot?.Stage.ToString() ?? "");
                    writer.Write(corpse?.InnerPawn?.Faction?.GetUniqueLoadID() ?? "");
                    writer.Write(corpse?.InnerPawn?.Name?.ToStringFull ?? "");
                }
                using (var hash = SHA256.Create())
                    return "waste-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        // Ports HomeWasteTools.Haul's WorkGiver scan: relocation to a dirty
        // outdoor stockpile cell, or burial in an empty grave. Never splits
        // or merges the observed stack.
        private static Job? FindJob(Pawn pawn, Thing thing, bool burialRequested)
        {
            foreach (var giver in WorkTypeDefOf.Hauling.workGiversByPriority)
            {
                if (giver == null || !giver.directOrderable || !(giver.Worker is WorkGiver_Scanner scanner)) continue;
                bool claims = scanner.PotentialWorkThingRequest.Accepts(thing) || (scanner.PotentialWorkThingsGlobal(pawn)?.Contains(thing) ?? false);
                if (!claims || scanner.ShouldSkip(pawn, true) || !scanner.HasJobOnThing(pawn, thing, true)) continue;
                var job = scanner.JobOnThing(pawn, thing, true);
                if (job == null || job.targetA.Thing != thing) continue;
                var grave = job.targetB.Thing as Building_Grave;
                bool burial = grave != null && !grave.HasCorpse && thing is Corpse;
                bool relocation = !burialRequested && job.def == JobDefOf.HaulToCell && job.targetB.IsValid
                    && DirtyCell(pawn.Map, job.targetB.Cell)
                    && (job.targetB.Cell.GetZone(pawn.Map) as Zone_Stockpile)?.GetStoreSettings().AllowedToAccept(thing) == true;
                if (relocation && (job.count < thing.stackCount || job.targetB.Cell.GetThingList(pawn.Map).Any(t => t.def == thing.def))) continue;
                if (!burial && !relocation) continue;
                if (!pawn.CanReach(job.targetB, PathEndMode.Touch, Danger.None)) continue;
                job.workGiverDef = giver;
                return job;
            }
            return null;
        }

        private static bool Prepare(Operations.ManageWaste command, Common.ObservationContext context, out NativeControlIdentity identity,
            out Pawn? pawn, out Thing? thing, out NativePawnSnapshot? snapshot, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, Find.CurrentMap, context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; thing = null; snapshot = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Waste order requires an exact pawn, exact target and bounded unwanted/bury lists.");
            if (!Valid(command)) return false;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Pawn.EntityId);
            if (pawn == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact colonist is not spawned on this map."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Pawn.ExpectedSnapshotToken, out snapshot);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            if (snapshot!.Drafted || !snapshot.Eligible)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Waste haul requires an eligible undrafted pawn."); return false; }
            thing = Find.CurrentMap.listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Target.EntityId);
            if (thing == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact waste item is unavailable."); return false; }
            if (Token(context.Identity, thing) != command.Target.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Waste target snapshot changed; observe before new admission."); return false; }
            bool burialRequested = Set(command.BuryIds).Contains(command.Target.EntityId);
            var protection = Protection(thing, burialRequested);
            var kind = burialRequested && thing is Corpse ? "corpse" : Kind(thing, Set(command.UnwantedIds));
            if (protection != null || kind == null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Item is protected or not eligible waste: " + (protection ?? "not eligible waste")); return false; }
            if (!burialRequested && Stored(thing))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Already relocated; no further haul needed."); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.ManageWaste; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Waste order requires an exact pawn, exact target and bounded unwanted/bury lists.");
            bool burialRequested = Set(command.BuryIds).Contains(command.Target.EntityId);
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var identity, out var pawn, out var thing, out var snapshot, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var result = FindJob(pawn!, thing!, burialRequested);
                if (result == null) return Refuse(Common.FailureCode.NativeFailure, "No native hauling job with an eligible separated storage or burial destination is available.");
                guard = authority.Check(pre.ExpectedGeneration);
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                if (!Prepare(command, context, out identity, out pawn, out thing, out snapshot, out failure))
                    return Refuse(Common.FailureCode.OwnerConflict, "Pawn or target snapshot changed before admission.");
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                bool accepted = false; Exception? effectError = null;
                using (authority.Owned())
                {
                    if (!NativePawnControlState.IsReady || !Prepare(command, context, out identity, out pawn, out thing, out snapshot, out failure))
                        throw new InvalidOperationException("Waste prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Waste authority changed before native effect.");
                    var job = FindJob(pawn!, thing!, burialRequested);
                    if (job == null) throw new InvalidOperationException("No native waste job is available after admission.");
                    var trackingId = HaulTracking.Begin(thing!, pawn!);
                    if (trackingId == null) throw new InvalidOperationException("Native haul quantity tracking is unavailable.");
                    var record = new NativeWasteRecord(identity, pawn!, thing!, job, trackingId, context);
                    state.Waste.Add(pre.Attempt.Clone(), record);
                    try { accepted = pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc); }
                    catch (Exception error) { effectError = error; }
                    if (NativePawnControlState.Observe(identity, pawn!, out snapshot) != NativePawnControlResult.Ready || snapshot == null)
                        throw new InvalidOperationException("Native waste readback unavailable.");
                    var current = pawn!.CurJob;
                    bool correlated = accepted && current != null && current.loadID == job.loadID;
                    HaulTracking.Accept(trackingId, correlated);
                    evidence = record.Evidence(snapshot, accepted, correlated);
                    if (effectError != null || !accepted || !correlated) throw new InvalidOperationException("Native waste order requires causal observation.", effectError);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Waste validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted waste order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.ManageWaste command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Waste order requires an exact pawn, exact target and bounded unwanted/bury lists.") };
            try
            {
                bool burialRequested = Set(command.BuryIds).Contains(command.Target.EntityId);
                if (!Prepare(command, context, out _, out var pawn, out var thing, out var snapshot, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                var result = FindJob(pawn!, thing!, burialRequested);
                var accepted = result != null;
                var jobDef = accepted ? (result!.def?.defName ?? "HaulToCell") : "HaulToCell";
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = accepted,
                        Reason = accepted ? "Exact native hauling WorkGiver produced a job for this waste target." : "No native hauling job with an eligible separated storage or burial destination is available.",
                        Projected = new Receipts.EffectEvidence
                        {
                            Job = new Receipts.JobEffect
                            {
                                PawnId = snapshot!.PawnId, JobDef = jobDef, CanTry = accepted, Issued = false, Verified = false,
                                TargetA = new Receipts.JobTarget { ThingId = thing!.GetUniqueLoadID() },
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Waste preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
