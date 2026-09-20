#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Mining counterpart to NativeHuntRecord: a Mineable has no back-reference
    // to its eventual output (unlike a hunted pawn's Corpse), so evidence is
    // polled the same way Hunt evidence is polled -- by observing whether the
    // designated source is gone and matching resource stacks appeared nearby
    // -- rather than tracked through Harmony hooks the way ordinary plant
    // harvest evidence (NativeAcquisitionRecord/NativeAcquisitionTracking) is.
    // A baseline of pre-existing nearby stacks (captured at admission time) is
    // subtracted so output already sitting near the deposit before this
    // attempt started is never misattributed. This is a disclosed narrowing,
    // not exact spawn tracking.
    internal sealed class NativeMineRecord : INativeAcquisitionRecord
    {
        private readonly Map map;
        private readonly IntVec3 cell;
        private readonly string source, resource;
        private readonly Dictionary<string, int> baseline;
        private readonly MiningRecord mining;
        private Receipts.AcquisitionEffect? completed;
        internal NativeMineRecord(Mineable rock)
        {
            map = rock.Map; cell = rock.Position; source = rock.GetUniqueLoadID();
            resource = rock.def.building.mineableThing.defName;
            baseline = NearbyStacks(map, cell, resource);
            MiningGuard.Install();
            mining = new MiningRecord { ThingId = rock.ThingID, SourceId = rock.ThingID,
                Definition = rock.def.defName, Resource = resource, MapId = map.uniqueID,
                X = cell.x, Z = cell.z, Started = Find.TickManager.TicksGame };
            MiningGuard.State().Records.Add(mining);
            mining.OnMined = () => completed = Evidence();
        }
        private static Dictionary<string, int> NearbyStacks(Map map, IntVec3 cell, string resource) =>
            GenRadial.RadialCellsAround(cell, 2, true).Where(c => c.InBounds(map) && !c.Fogged(map))
                .SelectMany(c => c.GetThingList(map)).Where(t => t.def.defName == resource)
                .ToDictionary(t => t.GetUniqueLoadID(), t => t.stackCount, StringComparer.Ordinal);
        internal Receipts.AcquisitionEffect Evidence()
        {
            if (completed != null) return completed.Clone();
            var stillThere = cell.InBounds(map) && cell.GetThingList(map).OfType<Mineable>().Any(m => m.GetUniqueLoadID() == source && m.Spawned);
            var finished = !stillThere;
            var result = new Receipts.AcquisitionEffect
            {
                SourceId = source, ResourceDef = resource, Cell = new Common.Cell { X = cell.x, Z = cell.z },
                Designated = !finished && map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) != null,
                LaborFinished = finished, ProducedUnits = 0, OutputComplete = false, OutputObserved = false,
            };
            if (!finished) return result;
            var nearby = GenRadial.RadialCellsAround(cell, 2, true).Where(c => c.InBounds(map) && !c.Fogged(map))
                .SelectMany(c => c.GetThingList(map)).Where(t => t.Spawned && t.def.defName == resource && !t.IsForbidden(Faction.OfPlayer)).ToList();
            long units = 0;
            foreach (var thing in nearby.OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                baseline.TryGetValue(thing.GetUniqueLoadID(), out var before);
                var delta = thing.stackCount - before;
                if (delta <= 0) continue;
                units += delta;
                result.Outputs.Add(new Receipts.AcquisitionOutput { ThingId = thing.GetUniqueLoadID(), Units = delta });
            }
            result.ProducedUnits = (int)Math.Min(int.MaxValue, units);
            result.OutputComplete = true;
            result.OutputObserved = units > 0;
            return result;
        }
        public Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context) => NativeAcquisitionRecord.Progress(attempt, context, Evidence());
    }

    internal static class NativeMineAcquisition
    {
        internal static bool IsMine(Operations.AcquireResource command, Common.ObservationContext context) => ProtoBoundary.ResolveMap(context)?.listerThings.AllThings.OfType<Mineable>().Any(m => m.GetUniqueLoadID() == command.Source?.EntityId) == true;
        internal static string Token(Common.Identity identity, string id, string resource, int x, int z, int hitPoints, int yield, bool designated)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                { writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId); writer.Write(id); writer.Write(resource); writer.Write(x); writer.Write(z); writer.Write(hitPoints); writer.Write(yield); writer.Write(designated); }
                using (var hash = SHA256.Create()) return "mine-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static Obs.SnapshotRef Snapshot(Mineable rock, Common.ObservationContext context) => new Obs.SnapshotRef {
            Context = context.Clone(), EntityId = rock.GetUniqueLoadID(), Token = Token(context.Identity, rock.GetUniqueLoadID(),
                rock.def.building.mineableThing.defName, rock.Position.x, rock.Position.z, rock.HitPoints, rock.def.building.mineableYield, ResourceAcquisitionTools.Designated(rock)) };
        internal const string Kind = "Mine";
        // Miner is the colonist rule ResourceAcquisitionTools.Eligible
        // applies to mining: someone must be able to do the work now.
        private static bool Miner(Pawn p, Mineable rock) => !p.Downed && !p.Drafted && !p.InMentalState
            && !p.WorkTypeIsDisabled(WorkTypeDefOf.Mining) && !rock.IsForbidden(p)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(rock, PathEndMode.Touch, Danger.None);
        // Prepare is the apply-time precondition list for mine
        // (action-contracts.md): ResourceAcquisitionTools.Eligible plus the
        // request's cell, resource and designation rules, one rule at a time;
        // the excavation-geometry rule reports MiningBlocker's own text.
        private static bool Prepare(Operations.AcquireResource command, Common.ObservationContext context, out Mineable? rock, out Common.Failure failure)
        {
            rock = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Mining requires an exact safe mineable snapshot, an eligible miner and safe excavation geometry.");
            if (!NativePlantAcquisition.Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var found = map.listerThings.AllThings.OfType<Mineable>().SingleOrDefault(m => m.GetUniqueLoadID() == command.Source.EntityId);
            var blocker = found == null ? null : BridgeCommon.Try(() => ResourceAcquisitionTools.MiningBlocker(found, map), "Unknown excavation geometry");
            var rules = new ApplyPreconditions(Kind)
                .Require(() => !map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)), "a roof collapse is pending on this map")
                .Present(() => found != null && found.Spawned, "the exact rock is no longer spawned on this map")
                .Require(() => found!.Position.x == command.Cell.X && found.Position.z == command.Cell.Z, "the rock is not at the expected cell")
                .Require(() => found!.def.building.mineableThing?.defName == command.ResourceDefName, "the rock no longer yields the expected resource")
                .Require(() => !found!.Position.Fogged(map), "the rock's cell is fogged")
                .Require(() => !found!.IsForbidden(Faction.OfPlayer), "the rock is forbidden")
                .Require(() => blocker == null, "excavation geometry is unsafe: " + blocker)
                .Require(() => !ResourceAcquisitionTools.Designated(found!), "the rock is already designated for mining")
                .Require(() => new Designator_Mine().CanDesignateThing(found!).Accepted, "the native mine designator refuses the rock")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Miner(p, found!)), "no free colonist able to mine can reach the rock")
                .Token(NativeDraftProtocol.TokenSent(command.Source), () => Snapshot(found!, context).Token == command.Source.ExpectedSnapshotToken, "the rock snapshot changed since it was read");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            rock = found;
            return true;
        }
        internal static Operations.PreviewReply Preview(Operations.AcquireResource command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.AcquireResource;
            try
            {
                if (!Prepare(command, context, out var rock, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                var record = new NativeMineRecord(rock!); state.Acquisition.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out var checkedRock, out failure) || !ReferenceEquals(rock, checkedRock)) throw new InvalidOperationException("Mining target changed before designation.");
                    new Designator_Mine().DesignateThing(rock);
                    evidence = new Receipts.EffectEvidence { Acquisition = record.Evidence() };
                    if (!evidence.Acquisition.Designated) throw new InvalidOperationException("Mining designation was not observed.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Mining write interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Mining failed: " + error.GetType().Name) };
            }
        }
    }
}
