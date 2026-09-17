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
                var crop = BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow").GetValue(growing) as ThingDef;
                if (crop != null) result.Snapshot.AfterToken = NativeZoneCreation.ConfigurationToken(new Operations.CreateZone {
                    Type = Operations.ZoneType.Growing, Label = Zone.label, Cells = new Operations.Cells { ExplicitCells = new Operations.CellList() },
                    Growing = new Operations.GrowingSettings { PlantDef = crop.defName, AllowSow = growing.allowSow, AllowCut = growing.allowCut } }, cells);
            }
            else if (Zone is Zone_Stockpile stockpile)
            {
                var universe = StockpileFilter.StorableDefs(stockpile);
                var priority = NativeZoneCreation.ToWirePriority(BridgeCommon.TryN(() => stockpile.settings.Priority));
                var filter = BridgeCommon.Try(() => stockpile.settings.filter, (ThingFilter?)null);
                var matchesFood = priority.HasValue && NativeZoneCreation.MatchesFoodPreset(filter, universe);
                if (matchesFood) result.Snapshot.AfterToken = NativeZoneCreation.ConfigurationToken(new Operations.CreateZone {
                    Type = Operations.ZoneType.Stockpile, Label = Zone.label, Cells = new Operations.Cells { ExplicitCells = new Operations.CellList() },
                    Stockpile = new Operations.StockpileSettings { Priority = priority!.Value, Preset = Operations.FilterPreset.Food } }, cells);
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
        internal static Operations.StoragePriority? ToWirePriority(RimWorld.StoragePriority? priority)
        {
            if (!priority.HasValue) return null;
            switch (priority.Value)
            {
                case RimWorld.StoragePriority.Low: return Operations.StoragePriority.Low;
                case RimWorld.StoragePriority.Normal: return Operations.StoragePriority.Normal;
                case RimWorld.StoragePriority.Preferred: return Operations.StoragePriority.Preferred;
                case RimWorld.StoragePriority.Important: return Operations.StoragePriority.Important;
                case RimWorld.StoragePriority.Critical: return Operations.StoragePriority.Critical;
                default: return null;
            }
        }
        // True only when the live filter's allowed set exactly equals the food
        // preset's def set over the same universe -- a partial or player-edited
        // filter must never be reported as matching a preset it does not equal.
        internal static bool MatchesFoodPreset(ThingFilter? filter, List<ThingDef> universe)
        {
            if (filter == null) return false;
            var expected = new HashSet<ThingDef>(StockpileFilter.PresetDefs("food", universe));
            var actual = StockpileFilter.AllowedSet(filter);
            return expected.SetEquals(actual);
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
            if (command.Type == Operations.ZoneType.Stockpile)
            {
                if (command.Growing != null || command.RequireCoveredEmpty || command.Stockpile == null
                    || !command.Stockpile.HasPriority || command.Stockpile.Priority != Operations.StoragePriority.Important || !command.Stockpile.HasPreset) return false;
                if (command.Stockpile.Preset == Operations.FilterPreset.Food)
                    return command.Label == "RimGovernor food storage" && command.Stockpile.Filter == null;
                // SecureSupplies' covered-storage fallback: an empty preset plus
                // an explicit allow-list of exact thing defs (the vulnerable
                // item), nothing else.
                if (command.Stockpile.Preset == Operations.FilterPreset.Nothing)
                    return command.Label == "RimGovernor supplies storage" && AllowListDefs(command.Stockpile.Filter) != null;
            }
            return false;
        }
        // AllowListDefs resolves a pure allow-list patch (no replace, no
        // disallow, 1..64 distinct existing storable thing defs) or returns null.
        private static List<ThingDef>? AllowListDefs(Operations.FilterPatch? filter)
        {
            if (filter == null || filter.Replace != null || filter.Disallow.Count != 0 || filter.Allow.Count == 0 || filter.Allow.Count > 64) return null;
            var defs = new List<ThingDef>();
            foreach (var selector in filter.Allow)
            {
                if (selector.DefinitionCase != Operations.FilterSelector.DefinitionOneofCase.ThingDef || !ProtoBoundary.IsIdentifier(selector.ThingDef)) return null;
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(selector.ThingDef);
                if (def == null || !def.EverStorable(false) || defs.Contains(def)) return null;
                defs.Add(def);
            }
            return defs;
        }
        private static bool Prepare(Operations.CreateZone command, Common.ObservationContext context, out ThingDef? crop, out Common.Failure failure)
        {
            crop = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires fresh free ground, an available configuration and an exact map snapshot.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.ResolveMap(context);
            if (MapSnapshot(map, context).Token != command.ExpectedMapSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires an exact map snapshot; the map changed since it was read."); return false; }
            var cells = command.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
            var selected = new HashSet<IntVec3>(cells);
            var reached = new HashSet<IntVec3> { cells[0] };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(cells[0]);
            while (queue.Count > 0) { var c = queue.Dequeue(); foreach (var offset in GenAdj.CardinalDirections) { var next = c + offset; if (selected.Contains(next) && reached.Add(next)) queue.Enqueue(next); } }
            if (reached.Count != selected.Count) return false;
            if (command.Type == Operations.ZoneType.Growing)
            {
                crop = DefDatabase<ThingDef>.GetNamedSilentFail(command.Growing.PlantDef);
                if (crop?.plant == null || !crop.plant.Sowable || crop.plant.harvestedThingDef?.IsNutritionGivingIngestible != true
                    || crop.researchPrerequisites?.Any(r => !r.IsFinished) == true || !PlantUtility.GrowthSeasonNow(map, crop)) return false;
                var designator = new Designator_ZoneAdd_Growing();
                var wanted = crop;
                return cells.All(c => c.InBounds(map) && !c.Fogged(map) && c.Walkable(map) && !c.Roofed(map)
                    && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t is Blueprint || t is Frame) && map.zoneManager.ZoneAt(c) == null && !map.zoneManager.AllZones.Any(z => z.Cells.Contains(c))
                    && !map.roofCollapseBuffer.IsMarkedToCollapse(c) && map.fertilityGrid.FertilityAt(c) >= wanted.plant.fertilityMin
                    && designator.CanDesignateCell(c).Accepted);
            }
            // A protected food store needs a roof and clear, empty, walkable floor;
            // the caller (a verified room) is responsible for the roof already
            // existing -- this only refuses ground that is not actually safe.
            foreach (var c in cells)
            {
                var gate = !c.InBounds(map) ? "out of bounds" : c.Fogged(map) ? "fogged" : !c.Walkable(map) ? "not walkable" : !c.Roofed(map) ? "unroofed"
                    : c.GetEdifice(map) != null ? "edifice" : c.GetThingList(map).Count != 0 ? "occupied by " + string.Join(",", c.GetThingList(map).Select(t => t.def.defName))
                    : map.zoneManager.ZoneAt(c) != null || map.zoneManager.AllZones.Any(z => z.Cells.Contains(c)) ? "already zoned"
                    : map.roofCollapseBuffer.IsMarkedToCollapse(c) ? "roof collapsing" : null;
                if (gate == null) continue;
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stockpile cell (" + c.x + "," + c.z + ") is not safe free ground: " + gate + ".");
                return false;
            }
            return true;
        }
        internal static Operations.PreviewReply Preview(Operations.CreateZone command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.CreateZone;
            try {
                if (!Prepare(command, context, out var crop, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!; handle = admitted.Handle;
                using (authority.Owned()) {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(command, context, out crop, out failure)) throw new InvalidOperationException("Zone scope changed before creation.");
                    var map = ProtoBoundary.ResolveMap(context);
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
                        var priority = ToNativePriority(command.Stockpile.Priority);
                        if (priority.HasValue) stockpile.settings.Priority = priority.Value;
                        var universe = StockpileFilter.StorableDefs(stockpile);
                        if (command.Stockpile.Preset == Operations.FilterPreset.Nothing)
                        {
                            stockpile.settings.filter.SetDisallowAll();
                            foreach (var def in AllowListDefs(command.Stockpile.Filter)!) stockpile.settings.filter.SetAllow(def, true);
                        }
                        else
                            StockpileFilter.Apply(stockpile.settings.filter, "food", new StockpileFilter.Resolved(), new StockpileFilter.Resolved(), StockpileFilter.ParentFilter(stockpile), universe, null);
                    }
                    evidence = new Receipts.EffectEvidence { Zone = record.Evidence() };
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error) {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Zone admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Created zone requires inspection: " + error.GetType().Name) };
            }
        }
    }
}
