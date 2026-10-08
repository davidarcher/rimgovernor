#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestMonuments
    {
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };

        internal static Obs.QuestMonument? Read(Quest quest)
        {
            var markers = quest.PartsListForReading.SelectMany(p => p.QuestLookTargets)
                .Select(t => t.Thing is MinifiedThing mini ? mini.InnerThing : t.Thing).OfType<MonumentMarker>()
                .Where(m => !m.Destroyed && (m.Spawned || m.ParentHolder is MinifiedThing packed && packed.Spawned)).Distinct().ToArray();
            if (markers.Length != 1) return null;
            var marker = markers[0];
            var pack = marker.ParentHolder as MinifiedThing;
            var map = marker.Spawned ? marker.Map : pack!.Map;
            var row = new Obs.QuestMonument { MarkerId = marker.GetUniqueLoadID(), DefName = marker.def.defName,
                MapId = map.uniqueID, Packed = pack != null, Installed = marker.Spawned, Complete = marker.complete,
                DisallowedTicks = marker.ticksSinceDisallowedBuilding };
            if (marker.Spawned)
            {
                row.AllDone = marker.AllDone;
                row.Cell = Cell(marker.Position);
                if (marker.FirstDisallowedBuilding != null) row.DisallowedBuildingId = marker.FirstDisallowedBuilding.GetUniqueLoadID();
            }
            else
            {
                // A bounded set of legal observations; Go chooses among them.
                foreach (var cell in map.AllCells.OrderBy(c => c.DistanceToSquared(map.Center)))
                {
                    if (cell.Fogged(map) || !GenConstruct.CanPlaceBlueprintAt(marker.def, cell, Rot4.North, map, false, pack, marker).Accepted) continue;
                    row.InstallCells.Add(Cell(cell));
                    if (row.InstallCells.Count == 32) break;
                }
            }
            var materials = new HashSet<ThingDef>();
            foreach (var entity in marker.sketch.Entities.OfType<SketchBuildable>())
            {
                var piece = new Obs.QuestMonumentPiece { DefName = entity.Buildable.defName, Stuff = entity.Stuff?.defName ?? "",
                    Offset = Cell(entity.pos), Rotation = entity is SketchThing thing ? thing.rot.AsInt : Rot4.North.AsInt };
                var stuffs = marker.AllowedStuffsFor(entity);
                if (entity.Buildable.MadeFromStuff) piece.AllowedStuffs.Add(stuffs.Select(s => s.defName));
                foreach (var cell in entity.OccupiedRect) piece.Footprint.Add(Cell(cell));
                foreach (var stuff in stuffs) materials.Add(stuff);
                if (!entity.Buildable.MadeFromStuff || entity.Stuff != null)
                    foreach (var cost in entity.Buildable.CostListAdjusted(entity.Stuff, false)) materials.Add(cost.thingDef);
                if (marker.Spawned)
                {
                    var cell = marker.Position + entity.pos;
                    piece.Built = entity.IsSameSpawned(cell, map);
                    piece.Queued = entity.IsSameSpawnedOrBlueprintOrFrame(cell, map);
                    piece.Allowed = !entity.IsSpawningBlocked(cell, map) && BuildCopyCommandUtility.FindAllowedDesignator(entity.Buildable) != null
                        && marker.AllowsPlacingBlueprint(entity.Buildable, cell, new Rot4(piece.Rotation), entity.Stuff);
                }
                row.Pieces.Add(piece);
            }
            var haulers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hauling) == true).ToArray();
            foreach (var item in map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && materials.Contains(t.def)
                && !t.Position.Fogged(map) && !t.IsForbidden(Faction.OfPlayer) && !t.IsInAnyStorage()).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var resource = new Obs.QuestMonumentResource { Id = item.GetUniqueLoadID(), DefName = item.def.defName, Cell = Cell(item.Position), InStorage = false };
                foreach (var pawn in haulers)
                    if (pawn.CanReserveAndReach(item, PathEndMode.Touch, Danger.None)
                        && StoreUtility.TryFindBestBetterStoreCellFor(item, pawn, map, StoragePriority.Unstored, Faction.OfPlayer, out _))
                        resource.EligibleHaulers.Add(pawn.GetUniqueLoadID());
                row.Resources.Add(resource);
            }
            return row;
        }
    }
}
