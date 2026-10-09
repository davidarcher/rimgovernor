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
        internal static bool Valid(Operations.ZoneIntent? command)
        {
            if (command == null || !command.HasKind || command.RemoveCells != null || command.Delete || command.AddCells?.ExplicitCells == null || command.AddCells.ExplicitCells.Cells.Count == 0) return false;
            var cells = command.AddCells.ExplicitCells.Cells;
            if (!cells.All(c => c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0) || cells.Select(c => Tuple.Create(c.X, c.Z)).Distinct().Count() != cells.Count) return false;
            if (command.Kind == Operations.ZoneType.Fishing)
                return command.Label == "Fishing" && command.Stockpile == null && command.Growing == null && !command.RequireCoveredEmpty
                    && command.Fishing != null && command.Fishing.HasPopulationFloor && command.Fishing.PopulationFloor >= 0 && command.Fishing.PopulationFloor <= 1
                    && (command.Zone == null || ProtoBoundary.IsIdentifier(command.Zone.Id));
            if (command.Fishing != null || command.Zone != null) return false;
            if (command.Kind == Operations.ZoneType.Growing)
                return command.Label == "Crops" && command.Stockpile == null && !command.RequireCoveredEmpty
                    && command.Growing != null && command.Growing.HasPlantDef && ProtoBoundary.IsIdentifier(command.Growing.PlantDef)
                    && command.Growing.HasAllowSow && command.Growing.AllowSow && command.Growing.HasAllowCut && command.Growing.AllowCut;
            return false;
        }
        internal const string Kind = "Zone creation";
        private static string At(IntVec3 c) => "(" + c.x + ", " + c.z + ")";
        private static IntVec3[] Cells(Operations.ZoneIntent command) => command.AddCells.ExplicitCells.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToArray();
        // Prepare separates a request that cannot be evaluated (malformed,
        // stale map snapshot, unresolvable configuration) from ground that
        // refuses the zone (ground true): the failure always names the rule,
        // and a preview reports a ground refusal as an evaluation the
        // controller can move past rather than a failed read. The ground
        // rules are the apply-time precondition list (action-contracts.md),
        // one rule per cell so a refusal names the cell that moved; a sent
        // whole-map zone census token is compared after them.
        internal static bool Prepare(Operations.ZoneIntent command, Common.ObservationContext context, out ThingDef? crop, out Common.Failure failure, out bool ground)
        {
            crop = null; ground = false; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires an available configuration and explicit cells.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var cells = Cells(command);
            var selected = new HashSet<IntVec3>(cells);
            var rules = new ApplyPreconditions(Kind);
            if (command.Kind == Operations.ZoneType.Fishing)
            {
                if (!ModsConfig.OdysseyActive || !ResearchProjectDefOf.Fishing.IsFinished)
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Fishing requires Odyssey and completed Fishing research."); return false; }
                var body = cells[0].InBounds(map) ? cells[0].GetWaterBody(map) : null;
                var existing = command.Zone != null ? RefIndex.Zone<Zone_Fishing>(map, command.Zone.Id) : null;
                if (command.Zone != null && (existing == null || existing.label != command.Label || !existing.Allowed || existing.Cells.Count >= cells.Length
                    || existing.Cells.Any(c => !selected.Contains(c) || map.zoneManager.ZoneAt(c) != existing || c.GetWaterBody(map) != body)))
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Fishing extension requires the exact zone and a larger complete footprint in the same body."); return false; }
                ground = true;
                foreach (var cell in cells)
                {
                    var c = cell;
                    rules.Require(() => existing != null && existing.Cells.Contains(c) || FishableCell(c, map, body), "cell " + At(c) + " is not free shallow fish-bearing water in this body");
                }
            }
            else if (command.Kind == Operations.ZoneType.Growing)
            {
                crop = DefDatabase<ThingDef>.GetNamedSilentFail(command.Growing.PlantDef);
                if (crop?.plant == null || !crop.plant.Sowable || !crop.plant.sowTags.Contains("Ground")
                    || crop.researchPrerequisites?.Any(r => !r.IsFinished) == true || !Command_SetPlantToGrow.IsPlantAvailable(crop, map)) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Zone creation requires an available crop."); return false; }
                var designator = new Designator_ZoneAdd_Growing();
                ground = true;
                // Legality is the game's own designator. Growing season,
                // fertility and roof decide whether a field is useful, which
                // is the controller's crop and cell choice, not a refusal.
                foreach (var cell in cells)
                {
                    var c = cell;
                    rules.Require(() => designator.CanDesignateCell(c).Accepted, "cell " + At(c) + " is refused by the native growing-zone designator");
                }
            }
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            ground = false;
            return true;
        }
        // StorageEmpty is the cell census's inventory occupancy fact. It is
        // used for stockpile fill, not vanilla stockpile placement eligibility.
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
        internal static Operations.ZonePreviewReply Preview(Operations.ZoneIntent command, Common.ObservationContext context)
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
        internal static Zone? Standing(Operations.ZoneIntent? command, Common.ObservationContext context)
        {
            if (!Valid(command)) return null;
            var map = ProtoBoundary.LoadedMap(context);
            var cells = Cells(command!);
            if (!cells[0].InBounds(map)) return null;
            var zone = map.zoneManager.ZoneAt(cells[0]);
            if (zone == null || zone.label != command!.Label || zone.Cells.Count != cells.Length || !cells.All(c => c.InBounds(map) && map.zoneManager.ZoneAt(c) == zone)) return null;
            switch (command.Kind)
            {
                case Operations.ZoneType.Fishing:
                    return zone is Zone_Fishing fishing && fishing.Allowed && fishing.repeatMode == FishRepeatMode.DoForever
                        && Math.Abs(fishing.targetPopulationPct - command.Fishing.PopulationFloor) < 0.000001
                        && (command.Zone == null || RefIndex.Is(zone, command.Zone.Id)) ? zone : null;
                case Operations.ZoneType.Growing:
                    if (!(zone is Zone_Growing growing) || !growing.allowSow || !growing.allowCut) return null;
                    var crop = (BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow") ?? throw new InvalidOperationException("Zone_Growing.plantDefToGrow is unavailable.")).GetValue(growing) as ThingDef;
                    return crop?.defName == command.Growing.PlantDef ? zone : null;
                default: return null;
            }
        }

        internal static Zone Create(Operations.ZoneIntent command, Map map, ThingDef? crop)
        {
            var cells = Cells(command);
            if (command.Kind == Operations.ZoneType.Fishing) {
                var fishing = command.Zone != null ? RefIndex.Zone<Zone_Fishing>(map, command.Zone.Id)! : new Zone_Fishing(map.zoneManager);
                if (command.Zone == null) map.zoneManager.RegisterZone(fishing);
                fishing.label = command.Label; fishing.repeatMode = FishRepeatMode.DoForever; fishing.targetPopulationPct = (float)command.Fishing.PopulationFloor;
                foreach (var cell in cells) if (!fishing.Cells.Contains(cell)) fishing.AddCell(cell);
                return fishing;
            }
            if (command.Kind == Operations.ZoneType.Growing) {
                var growing = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(growing); growing.label = command.Label;
                foreach (var cell in cells) growing.AddCell(cell);
                growing.SetPlantDefToGrow(crop!); growing.allowSow = true; growing.allowCut = true;
                return growing;
            }
            throw new InvalidOperationException("Unsupported exact zone creation.");
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

    // The zone intent's create shape: the ground rules of Prepare, checked live;
    // a zone that already is the request applies again.
    internal sealed class ZoneCreationActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            if (NativeZoneCreation.Standing(action.Zone, context) != null) return null;
            return NativeZoneCreation.Prepare(action.Zone, context, out _, out var failure, out _) ? null : failure;
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var command = action.Zone;
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
