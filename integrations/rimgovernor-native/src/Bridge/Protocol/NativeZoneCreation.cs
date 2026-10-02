#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeZoneCreation
    {
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
            if (command == null || command.Cells?.ExplicitCells == null || command.Cells.ExplicitCells.Cells.Count == 0) return false;
            var cells = command.Cells.ExplicitCells.Cells;
            if (!cells.All(c => c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0) || cells.Select(c => Tuple.Create(c.X, c.Z)).Distinct().Count() != cells.Count) return false;
            if (command.Type == Operations.ZoneType.Fishing)
                return command.Label == "Fishing" && command.Stockpile == null && command.Growing == null && !command.RequireCoveredEmpty
                    && command.Fishing != null && command.Fishing.HasPopulationFloor && command.Fishing.PopulationFloor == 0.6
                    && (!command.HasExtendZoneId || ProtoBoundary.IsIdentifier(command.ExtendZoneId));
            if (command.Fishing != null || command.HasExtendZoneId) return false;
            if (command.Type == Operations.ZoneType.Growing)
                return command.Label == "Crops" && command.Stockpile == null && !command.RequireCoveredEmpty
                    && command.Growing != null && command.Growing.HasPlantDef && ProtoBoundary.IsIdentifier(command.Growing.PlantDef)
                    && command.Growing.HasAllowSow && command.Growing.AllowSow && command.Growing.HasAllowCut && command.Growing.AllowCut;
            // A stockpile takes any label and any typed settings body; the
            // selectors resolve against the def database in Prepare.
            if (command.Type == Operations.ZoneType.Stockpile)
                return command.HasLabel && ProtoBoundary.IsIdentifier(command.Label) && command.Growing == null && !command.RequireCoveredEmpty
                    && command.Stockpile != null && command.Stockpile.HasPriority && NativeStockpileSettings.Valid(command.Stockpile);
            return false;
        }
        internal const string Kind = "Zone creation";
        private static string At(IntVec3 c) => "(" + c.x + ", " + c.z + ")";
        private static IntVec3[] Cells(Operations.CreateZone command) => command.Cells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
        // Prepare separates a request that cannot be evaluated (malformed,
        // stale map snapshot, unresolvable configuration) from ground that
        // refuses the zone (ground true): the failure always names the rule,
        // and a preview reports a ground refusal as an evaluation the
        // controller can move past rather than a failed read. The ground
        // rules are the apply-time precondition list (action-contracts.md),
        // one rule per cell so a refusal names the cell that moved; a sent
        // whole-map zone census token is compared after them.
        internal static bool Prepare(Operations.CreateZone command, Common.ObservationContext context, out ThingDef? crop, out Common.Failure failure, out bool ground)
        {
            crop = null; ground = false; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires an available configuration and explicit cells.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var cells = Cells(command);
            var selected = new HashSet<IntVec3>(cells);
            var reached = new HashSet<IntVec3> { cells[0] };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(cells[0]);
            while (queue.Count > 0) { var c = queue.Dequeue(); foreach (var offset in GenAdj.CardinalDirections) { var next = c + offset; if (selected.Contains(next) && reached.Add(next)) queue.Enqueue(next); } }
            if (reached.Count != selected.Count) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires cardinally connected cells."); return false; }
            var rules = new ApplyPreconditions(Kind);
            if (command.Type == Operations.ZoneType.Fishing)
            {
                if (!ModsConfig.OdysseyActive || DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Fishing")?.IsFinished != true)
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Fishing requires Odyssey and completed Fishing research."); return false; }
                var body = cells[0].InBounds(map) ? cells[0].GetWaterBody(map) : null;
                var existing = command.HasExtendZoneId ? map.zoneManager.AllZones.OfType<Zone_Fishing>().FirstOrDefault(z => z.GetUniqueLoadID() == command.ExtendZoneId) : null;
                if (command.HasExtendZoneId && (existing == null || existing.label != command.Label || !existing.Allowed || existing.Cells.Count >= cells.Length
                    || existing.Cells.Any(c => !selected.Contains(c) || map.zoneManager.ZoneAt(c) != existing || c.GetWaterBody(map) != body)))
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Fishing extension requires the exact zone and a larger complete footprint in the same body."); return false; }
                ground = true;
                foreach (var cell in cells)
                {
                    var c = cell;
                    rules.Require(() => existing != null && existing.Cells.Contains(c) || FishableCell(c, map, body), "cell " + At(c) + " is not free shallow fish-bearing water in this body");
                }
            }
            else if (command.Type == Operations.ZoneType.Growing)
            {
                crop = DefDatabase<ThingDef>.GetNamedSilentFail(command.Growing.PlantDef);
                if (crop?.plant == null || !crop.plant.Sowable || crop.plant.harvestedThingDef?.IsNutritionGivingIngestible != true
                    || crop.researchPrerequisites?.Any(r => !r.IsFinished) == true) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires an available crop."); return false; }
                var designator = new Designator_ZoneAdd_Growing();
                var wanted = crop;
                ground = true;
                // The growing season is each cell's own temperature, so a
                // heated greenhouse under a roof sows in winter while open
                // ground follows the outdoor season; the controller decides
                // whether a roofed cell is lit enough to be worth planting.
                foreach (var cell in cells)
                {
                    var c = cell;
                    rules.Require(() => c.InBounds(map) && !c.Fogged(map), "cell " + At(c) + " is out of bounds or fogged")
                        .Require(() => c.Walkable(map), "cell " + At(c) + " is not walkable")
                        .Require(() => PlantUtility.GrowthSeasonNow(c, map, wanted), "cell " + At(c) + " is outside the crop's growing season")
                        .Require(() => c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t is Blueprint || t is Frame), "cell " + At(c) + " holds a building, blueprint or frame")
                        .Require(() => map.zoneManager.ZoneAt(c) == null && !map.zoneManager.AllZones.Any(z => z.Cells.Contains(c)), "cell " + At(c) + " is already zoned")
                        .Require(() => !map.roofCollapseBuffer.IsMarkedToCollapse(c), "cell " + At(c) + " is marked for roof collapse")
                        .Require(() => map.fertilityGrid.FertilityAt(c) >= wanted.plant.fertilityMin, "cell " + At(c) + " is not fertile enough for the crop")
                        .Require(() => designator.CanDesignateCell(c).Accepted, "cell " + At(c) + " is refused by the native growing-zone designator");
                }
            }
            else
            {
                var resolved = NativeStockpileSettings.Resolve(command.Stockpile, StockpileFilter.StorableDefs(null));
                if (resolved == null) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires resolvable stockpile settings."); return false; }
                // Vanilla stockpiles need no roof, and where one belongs is
                // controller policy: a new colony's opening stockpiles sit on
                // open ground because no roof stands yet.
                // "Empty" is the cell census's own StorageEmpty: filth, a pawn or
                // a mote on the floor never made a stockpile cell unusable, and a
                // stricter check here refused every site the controller picked
                // from that census on a lived-in floor (#216, #223).
                ground = true;
                foreach (var cell in cells)
                {
                    var c = cell;
                    rules.Require(() => c.InBounds(map) && !c.Fogged(map) && c.Walkable(map)
                        && c.GetEdifice(map) == null && StorageEmpty(c, map)
                        && map.zoneManager.ZoneAt(c) == null && !map.zoneManager.AllZones.Any(z => z.Cells.Contains(c))
                        && !map.roofCollapseBuffer.IsMarkedToCollapse(c),
                        "fresh free ground required: cell " + At(c) + " is not walkable, unzoned, empty storage ground");
                }
            }
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            ground = false;
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

        // Vanilla's rule (Designator_ZoneAdd_Fishing.CanDesignateCell): any
        // passable cell of a fish-bearing water body (marsh included); zone
        // placement itself is IsZoneableCell below.
        internal static bool FishableCell(IntVec3 c, Map map, WaterBody? body)
        {
            return ModsConfig.OdysseyActive && body != null && body.HasFish && c.InBounds(map) && !c.Fogged(map)
                && c.GetWaterBody(map) == body && c.GetTerrain(map).passability != Traversability.Impassable
                && Designator_ZoneAdd.IsZoneableCell(c, map).Accepted && map.zoneManager.ZoneAt(c) == null
                && !map.zoneManager.AllZones.Any(z => z.Cells.Contains(c));
        }
        // A refused site is an evaluation with Accepted false, as placement
        // previews report one, so a site search previews per candidate and
        // moves on; only an unevaluable request is a failure.
        internal static Operations.ZonePreviewReply Preview(Operations.CreateZone command, Common.ObservationContext context)
        {
            var accepted = Prepare(command, context, out _, out var failure, out var ground);
            if (!accepted && !ground) return new Operations.ZonePreviewReply { Failure = failure };
            var evaluated = new Operations.ZonePreview { Context = context.Clone(), Accepted = accepted };
            if (!accepted) evaluated.Reason = failure.Detail;
            return new Operations.ZonePreviewReply { Evaluated = evaluated };
        }

        // Standing is the zone that already is the request: the zone on the
        // first cell covers exactly the requested cells (the grid agreeing),
        // under the requested label and configuration. A fishing extension
        // stands once the named zone covers the larger footprint.
        internal static Zone? Standing(Operations.CreateZone? command, Common.ObservationContext context)
        {
            if (!Valid(command)) return null;
            var map = ProtoBoundary.LoadedMap(context);
            var cells = Cells(command!);
            if (!cells[0].InBounds(map)) return null;
            var zone = map.zoneManager.ZoneAt(cells[0]);
            if (zone == null || zone.label != command!.Label || zone.Cells.Count != cells.Length || !cells.All(c => c.InBounds(map) && map.zoneManager.ZoneAt(c) == zone)) return null;
            switch (command.Type)
            {
                case Operations.ZoneType.Fishing:
                    return zone is Zone_Fishing fishing && fishing.Allowed && fishing.repeatMode == FishRepeatMode.DoForever
                        && Math.Abs(fishing.targetPopulationPct - command.Fishing.PopulationFloor) < 0.000001
                        && (!command.HasExtendZoneId || zone.GetUniqueLoadID() == command.ExtendZoneId) ? zone : null;
                case Operations.ZoneType.Growing:
                    if (!(zone is Zone_Growing growing) || !growing.allowSow || !growing.allowCut) return null;
                    var crop = (BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow") ?? throw new InvalidOperationException("Zone_Growing.plantDefToGrow is unavailable.")).GetValue(growing) as ThingDef;
                    return crop?.defName == command.Growing.PlantDef ? zone : null;
                default:
                    if (!(zone is Zone_Stockpile stockpile)) return null;
                    var resolved = NativeStockpileSettings.Resolve(command.Stockpile, StockpileFilter.StorableDefs(stockpile));
                    return resolved != null && NativeStockpileSettings.Matches(stockpile, resolved) ? zone : null;
            }
        }

        internal static Zone Create(Operations.CreateZone command, Map map, ThingDef? crop)
        {
            var cells = Cells(command);
            if (command.Type == Operations.ZoneType.Fishing) {
                var fishing = command.HasExtendZoneId ? map.zoneManager.AllZones.OfType<Zone_Fishing>().Single(z => z.GetUniqueLoadID() == command.ExtendZoneId) : new Zone_Fishing(map.zoneManager);
                if (!command.HasExtendZoneId) map.zoneManager.RegisterZone(fishing);
                fishing.label = command.Label; fishing.repeatMode = FishRepeatMode.DoForever; fishing.targetPopulationPct = (float)command.Fishing.PopulationFloor;
                foreach (var cell in cells) if (!fishing.Cells.Contains(cell)) fishing.AddCell(cell);
                return fishing;
            }
            if (command.Type == Operations.ZoneType.Growing) {
                var growing = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(growing); growing.label = command.Label;
                foreach (var cell in cells) growing.AddCell(cell);
                growing.SetPlantDefToGrow(crop!); growing.allowSow = true; growing.allowCut = true;
                return growing;
            }
            var stockpile = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(stockpile); stockpile.label = command.Label;
            foreach (var cell in cells) stockpile.AddCell(cell);
            var resolved = NativeStockpileSettings.Resolve(command.Stockpile, StockpileFilter.StorableDefs(stockpile))
                ?? throw new InvalidOperationException("Stockpile settings stopped resolving.");
            stockpile.settings.Priority = resolved.Priority!.Value;
            NativeStockpileSettings.Apply(stockpile.settings.filter, resolved, StockpileFilter.ParentFilter(stockpile), StockpileFilter.StorableDefs(stockpile));
            return stockpile;
        }

        // Evidence is one zone's identity, presence and cell count.
        internal static Receipts.EffectEvidence Evidence(string id, Zone? zone, Map map)
        {
            var present = zone != null && map.zoneManager.AllZones.Contains(zone);
            var effect = new Receipts.ZoneEffect { ZoneId = id, Present = present };
            if (present) effect.ListedCellCount = zone!.Cells.Count;
            return new Receipts.EffectEvidence { Zone = effect };
        }
    }

    // CreateZone on Actions/Apply: the ground rules of Prepare, checked live;
    // a zone that already is the request applies again.
    internal sealed class ZoneCreationActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            if (NativeZoneCreation.Standing(action.CreateZone, context) != null) return null;
            return NativeZoneCreation.Prepare(action.CreateZone, context, out _, out var failure, out _) ? null : failure;
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var command = action.CreateZone;
            var map = ProtoBoundary.LoadedMap(context);
            var zone = NativeZoneCreation.Standing(command, context);
            if (zone == null)
            {
                if (!NativeZoneCreation.Prepare(command, context, out var crop, out var failure, out _))
                    throw new InvalidOperationException("Zone creation prerequisites changed before apply: " + failure.Detail);
                zone = NativeZoneCreation.Create(command, map, crop);
                if (NativeZoneCreation.Standing(command, context) != zone) throw new InvalidOperationException("Native zone readback did not apply.");
            }
            return NativeZoneCreation.Evidence(zone.GetUniqueLoadID(), zone, map);
        }
    }
}
