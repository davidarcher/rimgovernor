#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Cell = RimGovernor.Brain.Domain.Cell;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using Rotation = RimGovernor.Brain.Domain.Rotation;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestMonuments
    {
        private static Cell CellOf(IntVec3 cell) => new Cell(cell.x, cell.z);

        private static Rotation RotationOf(Rot4 rot) => rot.AsInt switch
        {
            0 => Rotation.North,
            1 => Rotation.East,
            2 => Rotation.South,
            3 => Rotation.West,
            _ => throw new InvalidOperationException("Invalid sketch rotation " + rot.AsInt + "."),
        };

        private static Obs.QuestMonumentMaterial[] Materials(IEnumerable<Thing> things) => things
            .GroupBy(t => t.def).Select(g => new Obs.QuestMonumentMaterial(g.Key.defName, g.Sum(t => (long)t.stackCount))).ToArray();

        /// <summary>
        /// The build options of one sketch piece, the way MonumentMarker.AllowedStuffsFor and the sketch buildable do: a
        /// def made from stuff with none chosen offers every stuff that can make it, a chosen stuff offers only it, and a
        /// def not made from stuff has the one option with no stuff.
        /// </summary>
        private static Obs.QuestMonumentBuildOption[] Options(SketchBuildable entity, HashSet<ThingDef> materials)
        {
            var def = entity.Buildable;
            IEnumerable<ThingDef?> stuffs = def.MadeFromStuff && entity.Stuff == null
                ? GenStuff.AllowedStuffs(def.stuffCategories)
                : new[] { entity.Stuff };
            return stuffs.Select(stuff =>
            {
                var costs = def.CostListAdjusted(stuff, false);
                foreach (var cost in costs) materials.Add(cost.thingDef);
                return new Obs.QuestMonumentBuildOption
                {
                    Stuff = stuff?.defName,
                    Costs = [.. costs.Select(c => new Obs.QuestMonumentMaterial(c.thingDef.defName, c.count))],
                    Work = def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff),
                };
            }).ToArray();
        }

        /// <summary>The marker of a monument quest, or null when the quest holds no single live marker.</summary>
        internal static Obs.QuestMonument? Read(Quest quest)
        {
            var markers = quest.PartsListForReading.SelectMany(p => p.QuestLookTargets)
                .Select(t => t.Thing is MinifiedThing mini ? mini.InnerThing : t.Thing).OfType<MonumentMarker>()
                .Where(m => !m.Destroyed && (m.Spawned || m.ParentHolder is MinifiedThing packed && packed.Spawned)).Distinct().ToArray();
            var delivery = quest.PartsListForReading.OfType<QuestPart_DropPods>().Where(p => p.mapParent?.HasMap == true)
                .SelectMany(p => p.Things.Select(t => new { Part = p, Thing = t.GetInnerIfMinified() }))
                .Where(t => t.Thing is MonumentMarker && !t.Thing.Destroyed).ToArray();
            if (markers.Length == 0 && quest.State == QuestState.NotYetAccepted && delivery.Length == 1)
                markers = new[] { (MonumentMarker)delivery[0].Thing };
            if (markers.Length != 1) return null;
            var marker = markers[0];
            var pack = marker.ParentHolder as MinifiedThing;
            var offered = quest.State == QuestState.NotYetAccepted && !marker.Spawned && pack?.Spawned != true;
            var map = offered ? delivery[0].Part.mapParent.Map : marker.Spawned ? marker.Map : pack!.Map;
            Obs.QuestMonumentState state;
            if (marker.Spawned)
                state = new Obs.QuestMonumentState.Installed(CellOf(marker.Position), marker.AllDone, marker.FirstDisallowedBuilding?.GetUniqueLoadID());
            else
            {
                // A bounded set of legal observations; the brain chooses among them.
                var cells = map.AllCells.OrderBy(c => c.DistanceToSquared(map.Center))
                    .Where(cell => !cell.Fogged(map) && GenConstruct.CanPlaceBlueprintAt(marker.def, cell, Rot4.North, map, false, pack, marker).Accepted)
                    .Take(32).Select(CellOf).ToArray();
                state = offered
                    ? new Obs.QuestMonumentState.Offered([.. cells], [.. Materials(delivery[0].Part.Things
                        .Where(t => t.def.category == ThingCategory.Item && !(t.GetInnerIfMinified() is MonumentMarker)))])
                    : new Obs.QuestMonumentState.Packed([.. cells]);
            }
            var pieces = new List<Obs.QuestMonumentPiece>();
            var materials = new HashSet<ThingDef>();
            foreach (var entity in marker.sketch.Entities.OfType<SketchBuildable>())
            {
                var rotation = entity is SketchThing thing ? thing.rot : Rot4.North;
                Obs.QuestMonumentPieceSite site = new Obs.QuestMonumentPieceSite.Unplaced();
                if (marker.Spawned)
                {
                    var cell = marker.Position + entity.pos;
                    site = new Obs.QuestMonumentPieceSite.Placed(entity.IsSameSpawned(cell, map), entity.IsSameSpawnedOrBlueprintOrFrame(cell, map),
                        !entity.IsSpawningBlocked(cell, map) && BuildCopyCommandUtility.FindAllowedDesignator(entity.Buildable) != null
                        && marker.AllowsPlacingBlueprint(entity.Buildable, cell, rotation, entity.Stuff));
                }
                var options = Options(entity, materials);
                pieces.Add(new Obs.QuestMonumentPiece
                {
                    DefName = entity.Buildable.defName,
                    Offset = CellOf(entity.pos),
                    Rotation = RotationOf(rotation),
                    Footprint = [.. entity.OccupiedRect.Cells.Select(CellOf)],
                    Site = site,
                    Options = [.. options],
                });
            }
            var haulers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hauling) == true).ToArray();
            var available = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && materials.Contains(t.def)
                && !t.Position.Fogged(map) && !t.IsForbidden(Faction.OfPlayer)).ToArray();
            var resources = available.Where(t => !t.IsInAnyStorage()).OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal)
                .Select(item => new Obs.QuestMonumentResource
                {
                    Id = item.GetUniqueLoadID(),
                    DefName = item.def.defName,
                    Cell = CellOf(item.Position),
                    EligibleHaulers = [.. haulers.Where(pawn => pawn.CanReserveAndReach(item, PathEndMode.Touch, Danger.None)
                        && StoreUtility.TryFindBestBetterStoreCellFor(item, pawn, map, StoragePriority.Unstored, Faction.OfPlayer, out _))
                        .Select(pawn => new PawnId(pawn.GetUniqueLoadID()))],
                }).ToArray();
            return new Obs.QuestMonument
            {
                MarkerId = marker.GetUniqueLoadID(),
                DefName = marker.def.defName,
                MapId = map.uniqueID,
                State = state,
                Pieces = [.. pieces],
                Resources = [.. resources],
                AvailableResources = [.. Materials(available.Where(t => map.mapPawns.FreeColonistsSpawned.Any(p => p.CanReserveAndReach(t, PathEndMode.Touch, Danger.None))))],
            };
        }
    }
}
