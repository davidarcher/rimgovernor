#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using RimWorld;
using Verse;
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
        internal NativeMineRecord(Mineable rock)
        {
            map = rock.Map; cell = rock.Position; source = rock.GetUniqueLoadID();
            resource = rock.def.building.mineableThing.defName;
            baseline = NearbyStacks(map, cell, resource);
        }
        private static Dictionary<string, int> NearbyStacks(Map map, IntVec3 cell, string resource) =>
            GenRadial.RadialCellsAround(cell, 2, true).Where(c => c.InBounds(map) && !c.Fogged(map))
                .SelectMany(c => c.GetThingList(map)).Where(t => t.def.defName == resource)
                .ToDictionary(t => t.GetUniqueLoadID(), t => t.stackCount, StringComparer.Ordinal);
        internal Receipts.AcquisitionEffect Evidence()
        {
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
        private static bool Prepare(Operations.AcquireResource command, Common.ObservationContext context, out Mineable? rock, out Common.Failure failure)
        {
            rock = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Mining requires an exact safe mineable snapshot, an eligible miner and safe excavation geometry.");
            if (!NativePlantAcquisition.Valid(command)) return false;
            var map = ProtoBoundary.ResolveMap(context);
            if (map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c))) return false;
            rock = map.listerThings.AllThings.OfType<Mineable>().SingleOrDefault(m => m.GetUniqueLoadID() == command.Source.EntityId);
            return rock != null && ResourceAcquisitionTools.Eligible(rock, map) && rock.Position.x == command.Cell.X && rock.Position.z == command.Cell.Z
                && rock.def.building.mineableThing.defName == command.ResourceDefName && Snapshot(rock, context).Token == command.Source.ExpectedSnapshotToken
                && !ResourceAcquisitionTools.Designated(rock) && new Designator_Mine().CanDesignateThing(rock).Accepted;
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
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!;
                handle = admitted.Handle;
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
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Mining write interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Mining failed: " + error.GetType().Name) };
            }
        }
    }
}
