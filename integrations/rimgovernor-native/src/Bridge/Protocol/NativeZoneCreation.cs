#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Google.Protobuf;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeZoneRecord
    {
        internal readonly Zone_Growing Zone;
        internal readonly Map Map;
        internal readonly Operations.CreateZone Desired;
        internal NativeZoneRecord(Zone_Growing zone, Map map, Operations.CreateZone desired) { Zone = zone; Map = map; Desired = desired.Clone(); }
        internal Receipts.ZoneEffect Evidence()
        {
            var present = Map.zoneManager.AllZones.Contains(Zone);
            var cells = Zone.Cells.OrderBy(c => c.x).ThenBy(c => c.z).ToArray();
            var grid = Map.AllCells.Count(c => Map.zoneManager.ZoneAt(c) == Zone);
            var result = new Receipts.ZoneEffect { ZoneId = Zone.GetUniqueLoadID(), Present = present,
                ChangedCells = cells.Length, ListedCellCount = cells.Length, GridCellCount = grid,
                PhantomCellCount = cells.Count(c => Map.zoneManager.ZoneAt(c) != Zone),
                Snapshot = new Receipts.SnapshotEvidence { EntityId = Zone.GetUniqueLoadID(), BeforeToken = Desired.ExpectedMapSnapshotToken } };
            foreach (var c in cells) result.Cells.Add(new Receipts.CellResult { Cell = new Common.Cell { X = c.x, Z = c.z }, Accepted = Map.zoneManager.ZoneAt(c) == Zone });
            var crop = BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow").GetValue(Zone) as ThingDef;
            if (crop != null) result.Snapshot.AfterToken = NativeZoneCreation.ConfigurationToken(new Operations.CreateZone {
                Type = Operations.ZoneType.Growing, Label = Zone.label, Cells = new Operations.Cells { ExplicitCells = new Operations.CellList() },
                Growing = new Operations.GrowingSettings { PlantDef = crop.defName, AllowSow = Zone.allowSow, AllowCut = Zone.allowCut } }, cells);
            return result;
        }
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            var evidence = Evidence();
            if (Map != Find.CurrentMap || !evidence.Present) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Created zone is unavailable." }; return result; }
            var matches = evidence.PhantomCellCount == 0 && evidence.GridCellCount == evidence.ListedCellCount
                && evidence.Snapshot.AfterToken == NativeZoneCreation.ConfigurationToken(Desired);
            if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Zone = evidence } };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                Evidence = new Receipts.EffectEvidence { Zone = evidence }, Detail = "Zone cells or crop settings differ from the admitted configuration." };
            return result;
        }
    }

    internal static class NativeZoneCreation
    {
        private static string Hash(byte[] data) { using (var hash = SHA256.Create()) return "zone-" + BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        internal static Obs.SnapshotRef MapSnapshot(Map map, Common.ObservationContext context)
        {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
                    writer.Write(context.Identity.ColonyId); writer.Write(context.Identity.LoadToken); writer.Write(map.uniqueID); writer.Write(context.Tick);
                    if (map.zoneManager.AllZones.Count > 256 || map.zoneManager.AllZones.Sum(z => (long)z.Cells.Count) > 65536) throw new InvalidOperationException("Zone census exceeds bound.");
                    foreach (var zone in map.zoneManager.AllZones.OrderBy(z => z.GetUniqueLoadID(), StringComparer.Ordinal)) {
                        writer.Write(zone.GetUniqueLoadID()); writer.Write(zone.Cells.Count);
                        foreach (var cell in zone.Cells.OrderBy(c => c.x).ThenBy(c => c.z)) { writer.Write(cell.x); writer.Write(cell.z); }
                    }
                }
                return new Obs.SnapshotRef { Context = context.Clone(), EntityId = "map-" + map.uniqueID, Token = Hash(stream.ToArray()) };
            }
        }
        internal static string ConfigurationToken(Operations.CreateZone command, IntVec3[]? actual = null)
        {
            var copy = command.Clone(); copy.ClearExpectedMapSnapshotToken(); copy.ClearRequireCoveredEmpty();
            var cells = actual ?? command.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
            copy.Cells = new Operations.Cells { ExplicitCells = new Operations.CellList() };
            foreach (var cell in cells.OrderBy(c => c.x).ThenBy(c => c.z)) copy.Cells.ExplicitCells.Cells.Add(new Common.Cell { X = cell.x, Z = cell.z });
            return Hash(copy.ToByteArray());
        }
        internal static bool Valid(Operations.CreateZone? command)
        {
            if (command == null || !command.HasExpectedMapSnapshotToken || !ProtoBoundary.IsIdentifier(command.ExpectedMapSnapshotToken)
                || command.Type != Operations.ZoneType.Growing || command.Label != "RimGovernor crops" || command.Stockpile != null
                || command.RequireCoveredEmpty || command.Growing == null || !command.Growing.HasPlantDef || !ProtoBoundary.IsIdentifier(command.Growing.PlantDef)
                || !command.Growing.HasAllowSow || !command.Growing.AllowSow || !command.Growing.HasAllowCut || !command.Growing.AllowCut
                || command.Cells?.ExplicitCells == null || command.Cells.ExplicitCells.Cells.Count == 0 || command.Cells.ExplicitCells.Cells.Count > 256) return false;
            var cells = command.Cells.ExplicitCells.Cells;
            return cells.All(c => c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0) && cells.Select(c => Tuple.Create(c.X,c.Z)).Distinct().Count() == cells.Count;
        }
        private static bool Prepare(Operations.CreateZone command, Common.ObservationContext context, out ThingDef? crop, out Common.Failure failure)
        {
            crop = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Growing zone requires fresh free soil, an available crop and an exact map snapshot.");
            if (!Valid(command)) return false;
            var map = Find.CurrentMap;
            if (MapSnapshot(map,context).Token != command.ExpectedMapSnapshotToken) return false;
            crop = DefDatabase<ThingDef>.GetNamedSilentFail(command.Growing.PlantDef);
            if (crop?.plant == null || !crop.plant.Sowable || crop.plant.harvestedThingDef?.IsNutritionGivingIngestible != true
                || crop.researchPrerequisites?.Any(r => !r.IsFinished) == true || !PlantUtility.GrowthSeasonNow(map,crop)) return false;
            var cells = command.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X,0,c.Z)).ToArray();
            var selected = new System.Collections.Generic.HashSet<IntVec3>(cells);
            var reached = new System.Collections.Generic.HashSet<IntVec3> { cells[0] }; var queue = new System.Collections.Generic.Queue<IntVec3>(); queue.Enqueue(cells[0]);
            while (queue.Count > 0) { var c = queue.Dequeue(); foreach (var offset in GenAdj.CardinalDirections) { var next = c + offset; if (selected.Contains(next) && reached.Add(next)) queue.Enqueue(next); } }
            if (reached.Count != selected.Count) return false;
            var designator = new Designator_ZoneAdd_Growing();
            var wanted = crop;
            return cells.All(c => c.InBounds(map) && !c.Fogged(map) && c.Walkable(map) && !c.Roofed(map)
                && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t is Blueprint || t is Frame) && map.zoneManager.ZoneAt(c) == null && !map.zoneManager.AllZones.Any(z => z.Cells.Contains(c))
                && !map.roofCollapseBuffer.IsMarkedToCollapse(c) && map.fertilityGrid.FertilityAt(c) >= wanted.plant.fertilityMin
                && designator.CanDesignateCell(c).Accepted);
        }
        internal static Operations.PreviewReply Preview(Operations.CreateZone command, Common.ObservationContext context)
        {
            if (!Prepare(command,context,out _,out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Authority.Owner? owner = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.CreateZone;
            try {
                if (!Prepare(command,context,out var crop,out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game,out var authority) || authority == null) return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired,"Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration,pre.LeaseId,pre.Attempt.ControllerSessionId); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error,context) };
                owner = new Authority.Owner { ControllerSessionId = guard.Snapshot.Lease!.ControllerSessionId, PlayerDirection = guard.Snapshot.Lease.PlayerDirection };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute",request,context,owner);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!; handle = admitted.Handle;
                using (authority.Owned()) {
                    if (!authority.Check(pre.ExpectedGeneration,pre.LeaseId,pre.Attempt.ControllerSessionId).Success || !Prepare(command,context,out crop,out failure)) throw new InvalidOperationException("Zone scope changed before creation.");
                    var map = Find.CurrentMap; var zone = new Zone_Growing(map.zoneManager); var record = new NativeZoneRecord(zone,map,command);
                    state.Zones.Add(pre.Attempt.Clone(),record); map.zoneManager.RegisterZone(zone); zone.label = command.Label;
                    foreach (var c in command.Cells.ExplicitCells.Cells) zone.AddCell(new IntVec3(c.X,0,c.Z));
                    zone.SetPlantDefToGrow(crop!); zone.allowSow = true; zone.allowCut = true;
                    evidence = new Receipts.EffectEvidence { Zone = record.Evidence() };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger,handle,pre.Attempt,context,owner,evidence) };
            }
            catch (Exception error) {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure,"Zone admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger,handle,pre.Attempt,context,owner!,evidence!,"Created zone requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
