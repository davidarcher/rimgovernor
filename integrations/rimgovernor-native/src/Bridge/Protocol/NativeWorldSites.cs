#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeWorldSites
    {
        internal static IEnumerable<Obs.WorldSite> Read(Common.ObservationContext context)
        {
            var quests = Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI && !q.Historical && !q.dismissed).ToArray();
            var targets = quests.SelectMany(q => q.QuestLookTargets).Where(t => t.HasWorldObject).Select(t => t.WorldObject).Distinct().ToArray();
            var origin = Find.Maps.FirstOrDefault(m => context.Identity.HasMapId && m.uniqueID == context.Identity.MapId);
            foreach (var world in Find.WorldObjects.AllWorldObjects.OfType<Site>().Cast<WorldObject>().Concat(targets).Distinct().OrderBy(w => w.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var mapParent = world as MapParent;
                var site = world as Site;
                var row = new Obs.WorldSite { Id = world.GetUniqueLoadID(), DefName = world.def.defName,
                    Label = site != null && site.parts.Count == 0 ? world.def.label ?? "" : world.Label ?? "",
                    State = world.Destroyed ? Obs.WorldSiteState.Destroyed : mapParent?.HasMap == true ? Obs.WorldSiteState.MapLoaded
                        : world.Spawned ? Obs.WorldSiteState.Spawned : Obs.WorldSiteState.Pending };
                if (world.Tile.Valid)
                {
                    row.Tile = world.Tile.tileId; row.LayerId = world.Tile.Layer.LayerID;
                    if (origin != null && origin.Tile.Layer == world.Tile.Layer)
                        row.DistanceTiles = Find.WorldGrid.ApproxDistanceInTiles(origin.Tile, world.Tile);
                }
                if (mapParent?.HasMap == true) row.MapId = mapParent.Map.uniqueID;
                if (origin != null && world.Tile.Valid && world.Tile.Layer.IsRootSurface && origin.Tile.Layer.IsRootSurface)
                    Route(origin, world, row);
                row.QuestIds.Add(quests.Where(q => q.QuestLookTargets.Any(t => t.HasWorldObject && t.WorldObject == world)).Select(q => q.GetUniqueLoadID()));
                if (mapParent?.HasMap == true)
                    row.Threat = GenHostility.AnyHostileActiveThreatToPlayer(mapParent.Map, countDormantPawnsAsHostile: true, canBeFogged: true);
                else if (site != null && site.sitePartsKnown && site.parts.All(p => !p.hidden))
                {
                    row.ThreatPoints = site.ActualThreatPoints;
                    row.Threat = site.ActualThreatPoints > 0;
                }
                yield return row;
            }
        }

        // A full-mass, slowest-singleton estimate bounds any human-only subset
        // of this observed crew, without assuming an averaged caravan bonus.
        private static void Route(Map origin, WorldObject target, Obs.WorldSite row)
        {
            var pawns = origin.mapPawns.FreeColonistsSpawned.Where(p => NativeCaravanCatalog.PawnEligible(p)
                && p.DevelopmentalStage.Adult()).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            if (pawns.Count == 0) return;
            using var path = origin.Tile.Layer.Pather.FindPath(origin.Tile, target.Tile, null);
            row.Reachable = path.Found;
            if (!path.Found) return;
            var ticksPerMove = pawns.Max(p => CaravanTicksPerMoveUtility.GetTicksPerMove(new List<Pawn> { p }, 1, 1));
            row.TravelTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(origin.Tile, target.Tile, path, 0, ticksPerMove, GenTicks.TicksAbs);
            row.RoutePawnIds.Add(pawns.Select(p => p.GetUniqueLoadID()));
        }
    }
}
