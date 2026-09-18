#nullable enable
using System;
using System.Collections.Generic;
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
        internal readonly Zone Zone;
        internal readonly Map Map;
        internal readonly Operations.CreateZone Desired;
        internal NativeZoneRecord(Zone zone, Map map, Operations.CreateZone desired) { Zone = zone; Map = map; Desired = desired.Clone(); }
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
            if (Zone is Zone_Growing growing)
            {
                var crop = (BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow") ?? throw new InvalidOperationException("Zone_Growing.plantDefToGrow is unavailable.")).GetValue(growing) as ThingDef;
                if (crop != null) result.Snapshot.AfterToken = NativeZoneCreation.ConfigurationToken(new Operations.CreateZone {
                    Type = Operations.ZoneType.Growing, Label = Zone.label, Cells = new Operations.Cells { ExplicitCells = new Operations.CellList() },
                    Growing = new Operations.GrowingSettings { PlantDef = crop.defName, AllowSow = growing.allowSow, AllowCut = growing.allowCut } }, cells);
            }
            else if (Zone is Zone_Stockpile stockpile)
            {
                // The after-token is the admitted configuration only while the
                // live settings still equal it; a player-edited filter or
                // priority reports no after-token rather than a false match.
                var resolved = BridgeCommon.Try(() => NativeStockpileSettings.Resolve(Desired.Stockpile, StockpileFilter.StorableDefs(stockpile)), null);
                var matches = resolved != null && Zone.label == Desired.Label && BridgeCommon.Try(() => NativeStockpileSettings.Matches(stockpile, resolved), false);
                if (matches) result.Snapshot.AfterToken = NativeZoneCreation.ConfigurationToken(Desired, cells);
            }
            return result;
        }
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            var evidence = Evidence();
            if (Map != ProtoBoundary.ResolveMap(context) || !evidence.Present) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Created zone is unavailable." }; return result; }
            var matches = evidence.PhantomCellCount == 0 && evidence.GridCellCount == evidence.ListedCellCount
                && evidence.Snapshot.AfterToken == NativeZoneCreation.ConfigurationToken(Desired);
            if (matches) result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Zone = evidence } };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                Evidence = new Receipts.EffectEvidence { Zone = evidence }, Detail = "Zone cells or settings differ from the admitted configuration." };
            return result;
        }
    }

    internal static class NativeZoneCreation
    {
        private static string Hash(byte[] data) { using (var hash = SHA256.Create()) return "zone-" + BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        // Whole-map CAS token for CreateZone: identity plus the zone census
        // (every zone's id and cells), never the tick. The controller reads it
        // from colony facts and previews on a running clock (#150), so a token
        // that hashed the tick could only ever match on a paused map; the cell
        // conditions themselves are re-checked at preview and execute.
        internal static Obs.SnapshotRef MapSnapshot(Map map, Common.ObservationContext context)
        {
            using (var stream = new MemoryStream()) {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
                    writer.Write(context.Identity.ColonyId); writer.Write(context.Identity.LoadToken); writer.Write(map.uniqueID);
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
        // RimWorld.StoragePriority is a plain int enum (Unstored=0, Low..Critical);
        // the wire enum mirrors it one-for-one except Unstored has no wire member.
        internal static RimWorld.StoragePriority? ToNativePriority(Operations.StoragePriority priority)
        {
            switch (priority)
            {
                case Operations.StoragePriority.Low: return RimWorld.StoragePriority.Low;
                case Operations.StoragePriority.Normal: return RimWorld.StoragePriority.Normal;
                case Operations.StoragePriority.Preferred: return RimWorld.StoragePriority.Preferred;
                case Operations.StoragePriority.Important: return RimWorld.StoragePriority.Important;
                case Operations.StoragePriority.Critical: return RimWorld.StoragePriority.Critical;
                default: return null;
            }
        }
        internal static bool Valid(Operations.CreateZone? command)
        {
            if (command == null || !command.HasExpectedMapSnapshotToken || !ProtoBoundary.IsIdentifier(command.ExpectedMapSnapshotToken)
                || command.Cells?.ExplicitCells == null || command.Cells.ExplicitCells.Cells.Count == 0 || command.Cells.ExplicitCells.Cells.Count > 256) return false;
            var cells = command.Cells.ExplicitCells.Cells;
            if (!cells.All(c => c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0) || cells.Select(c => Tuple.Create(c.X, c.Z)).Distinct().Count() != cells.Count) return false;
            if (command.Type == Operations.ZoneType.Growing)
                return command.Label == "RimGovernor crops" && command.Stockpile == null && !command.RequireCoveredEmpty
                    && command.Growing != null && command.Growing.HasPlantDef && ProtoBoundary.IsIdentifier(command.Growing.PlantDef)
                    && command.Growing.HasAllowSow && command.Growing.AllowSow && command.Growing.HasAllowCut && command.Growing.AllowCut;
            // A stockpile takes any label and any typed settings body; the
            // selectors resolve against the def database in Prepare.
            if (command.Type == Operations.ZoneType.Stockpile)
                return command.HasLabel && ProtoBoundary.IsIdentifier(command.Label) && command.Growing == null && !command.RequireCoveredEmpty
                    && command.Stockpile != null && command.Stockpile.HasPriority && NativeStockpileSettings.Valid(command.Stockpile);
            return false;
        }
        // Prepare separates a request that cannot be evaluated (malformed,
        // stale map snapshot, unresolvable configuration) from ground that
        // refuses the zone (ground true): the failure always names the rule,
        // and a preview reports a ground refusal as an evaluation the
        // controller can move past rather than a failed read.
        private static bool Prepare(Operations.CreateZone command, Common.ObservationContext context, out ThingDef? crop, out Common.Failure failure, out bool ground)
        {
            crop = null; ground = false; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires an available configuration and an exact map snapshot.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            if (MapSnapshot(map, context).Token != command.ExpectedMapSnapshotToken) return false;
            var cells = command.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
            var selected = new HashSet<IntVec3>(cells);
            var reached = new HashSet<IntVec3> { cells[0] };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(cells[0]);
            while (queue.Count > 0) { var c = queue.Dequeue(); foreach (var offset in GenAdj.CardinalDirections) { var next = c + offset; if (selected.Contains(next) && reached.Add(next)) queue.Enqueue(next); } }
            if (reached.Count != selected.Count) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires cardinally connected cells."); return false; }
            if (command.Type == Operations.ZoneType.Growing)
            {
                crop = DefDatabase<ThingDef>.GetNamedSilentFail(command.Growing.PlantDef);
                if (crop?.plant == null || !crop.plant.Sowable || crop.plant.harvestedThingDef?.IsNutritionGivingIngestible != true
                    || crop.researchPrerequisites?.Any(r => !r.IsFinished) == true) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires an available crop."); return false; }
                var designator = new Designator_ZoneAdd_Growing();
                var wanted = crop;
                ground = true; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires fresh free ground in the growing season.");
                // The growing season is each cell's own temperature, so a
                // heated greenhouse under a roof sows in winter while open
                // ground follows the outdoor season; the controller decides
                // whether a roofed cell is lit enough to be worth planting.
                return cells.All(c => c.InBounds(map) && !c.Fogged(map) && c.Walkable(map) && PlantUtility.GrowthSeasonNow(c, map, wanted)
                    && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t is Blueprint || t is Frame) && map.zoneManager.ZoneAt(c) == null && !map.zoneManager.AllZones.Any(z => z.Cells.Contains(c))
                    && !map.roofCollapseBuffer.IsMarkedToCollapse(c) && map.fertilityGrid.FertilityAt(c) >= wanted.plant.fertilityMin
                    && designator.CanDesignateCell(c).Accepted);
            }
            if (NativeStockpileSettings.Resolve(command.Stockpile, StockpileFilter.StorableDefs(null)) == null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires resolvable stockpile settings."); return false; }
            // A protected store needs a roof and clear, empty, walkable floor;
            // the caller (a verified room) is responsible for the roof already
            // existing -- this only refuses ground that is not actually safe.
            // "Empty" is the cell census's own StorageEmpty: filth, a pawn or
            // a mote on the floor never made a stockpile cell unusable, and a
            // stricter check here refused every site the controller picked
            // from that census on a lived-in floor (#216, #223).
            ground = true;
            foreach (var c in cells) {
                if (!(c.InBounds(map) && !c.Fogged(map) && c.Walkable(map) && c.Roofed(map)
                    && c.GetEdifice(map) == null && StorageEmpty(c, map)
                    && map.zoneManager.ZoneAt(c) == null && !map.zoneManager.AllZones.Any(z => z.Cells.Contains(c))
                    && !map.roofCollapseBuffer.IsMarkedToCollapse(c))) {
                    failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires fresh free ground: cell (" + c.x + ", " + c.z + ") is not roofed, walkable, unzoned, empty storage ground.");
                    return false;
                }
            }
            return true;
        }
        // StorageEmpty is the one definition of a cell with nothing stored or
        // built on it, shared by the cell census (CellState.StorageEmpty,
        // colony facts) and stockpile zone creation so a site the controller
        // chose from the census is the site native accepts.
        internal static bool StorageEmpty(IntVec3 c, Map map)
        {
            return !c.GetThingList(map).Any(t => t is Plant || t is Building || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item);
        }
        // A refused site is an evaluation with Accepted false, as placement
        // previews report one, so a site search previews per candidate and
        // moves on; only an unevaluable request is a failure.
        internal static Operations.PreviewReply Preview(Operations.CreateZone command, Common.ObservationContext context)
        {
            var accepted = Prepare(command, context, out _, out var failure, out var ground);
            if (!accepted && !ground) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = accepted } };
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.CreateZone;
            try {
                if (!Prepare(command, context, out var crop, out var failure, out _)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply; handle = admitted.AdmittedHandle;
                using (authority.Owned()) {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(command, context, out crop, out failure, out _)) throw new InvalidOperationException("Zone scope changed before creation.");
                    var map = ProtoBoundary.LoadedMap(context);
                    NativeZoneRecord record;
                    if (command.Type == Operations.ZoneType.Growing) {
                        var growing = new Zone_Growing(map.zoneManager);
                        record = new NativeZoneRecord(growing, map, command);
                        state.Zones.Add(pre.Attempt.Clone(), record);
                        map.zoneManager.RegisterZone(growing); growing.label = command.Label;
                        foreach (var c in command.Cells.ExplicitCells.Cells) growing.AddCell(new IntVec3(c.X, 0, c.Z));
                        growing.SetPlantDefToGrow(crop!); growing.allowSow = true; growing.allowCut = true;
                    } else {
                        var stockpile = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                        record = new NativeZoneRecord(stockpile, map, command);
                        state.Zones.Add(pre.Attempt.Clone(), record);
                        map.zoneManager.RegisterZone(stockpile); stockpile.label = command.Label;
                        foreach (var c in command.Cells.ExplicitCells.Cells) stockpile.AddCell(new IntVec3(c.X, 0, c.Z));
                        var resolved = NativeStockpileSettings.Resolve(command.Stockpile, StockpileFilter.StorableDefs(stockpile))
                            ?? throw new InvalidOperationException("Stockpile settings stopped resolving.");
                        stockpile.settings.Priority = resolved.Priority!.Value;
                        NativeStockpileSettings.Apply(stockpile.settings.filter, resolved, StockpileFilter.ParentFilter(stockpile), StockpileFilter.StorableDefs(stockpile));
                    }
                    evidence = new Receipts.EffectEvidence { Zone = record.Evidence() };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error) {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Zone admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Created zone requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
