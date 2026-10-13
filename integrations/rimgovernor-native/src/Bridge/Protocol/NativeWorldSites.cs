#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeWorldSites
    {
        internal static IEnumerable<Obs.WorldSite> Read(Common.ObservationContext context)
        {
            var quests = Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI && !q.Historical && !q.dismissed).ToArray();
            var targets = quests.SelectMany(q => q.QuestLookTargets).Where(t => t.HasWorldObject).Select(t => t.WorldObject).Distinct().ToArray();
            var origin = Find.Maps.FirstOrDefault(m => context.Identity?.MapId is not null && m.uniqueID == context.Identity.MapId.GetValueOrDefault());
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
                    row = row with { Tile = world.Tile.tileId }; row = row with { LayerId = world.Tile.Layer.LayerID };
                    if (origin != null && origin.Tile.Layer == world.Tile.Layer)
                        row = row with { DistanceTiles = Find.WorldGrid.ApproxDistanceInTiles(origin.Tile, world.Tile) };
                }
                if (mapParent?.HasMap == true) row = row with { MapId = mapParent.Map.uniqueID };
                if (origin != null && world.Tile.Valid && world.Tile.Layer.IsRootSurface && origin.Tile.Layer.IsRootSurface)
                    row = Route(origin, world, row);
                row = row with { QuestIds = [.. row.QuestIds, .. quests.Where(q => q.QuestLookTargets.Any(t => t.HasWorldObject && t.WorldObject == world)).Select(q => q.GetUniqueLoadID())] };
                if (site != null) row = row with { Security = NativeSiteSecurity.Read(site, quests) };
                if (site != null) row = row with { MiningTargets = [.. row.MiningTargets, .. NativeQuestSiteMining.Read(site)] };
                if (mapParent?.HasMap == true) row = row with { Extraction = NativeSiteExtraction.Read(mapParent.Map) };
                if (world is PeaceTalks && world.Faction != null)
                {
                    var goodwill = Faction.OfPlayer.GoodwillWith(world.Faction);
                    row = row with { PeaceTalks = new Obs.QuestPeaceTalks { FactionId = world.Faction.GetUniqueLoadID(), CurrentGoodwill = goodwill,
                        WorstGoodwillLoss = -System.Math.Min(DiplomacyTuning.Goodwill_PeaceTalksDisasterRange.min, Faction.OfPlayer.GoodwillToMakeHostile(world.Faction)),
                        BestGoodwillGain = System.Math.Min(DiplomacyTuning.Goodwill_PeaceTalksTriumphRange.max, DiplomacyTuning.MaxGoodwill - goodwill), IdeologyActive = ModsConfig.IdeologyActive } };
                }
                if (mapParent?.HasMap == true)
                    row = row with { Threat = GenHostility.AnyHostileActiveThreatToPlayer(mapParent.Map, countDormantPawnsAsHostile: true, canBeFogged: true) };
                else if (site != null && site.sitePartsKnown && site.parts.All(p => !p.hidden))
                {
                    row = row with { ThreatPoints = site.ActualThreatPoints };
                    row = row with { Threat = site.ActualThreatPoints > 0 };
                }
                yield return row;
            }
        }

        // A full-mass, slowest-singleton estimate bounds any human-only subset
        // of this observed crew, without assuming an averaged caravan bonus.
        private static Obs.WorldSite Route(Map origin, WorldObject target, Obs.WorldSite row)
        {
            var pawns = origin.mapPawns.FreeColonistsSpawned.Where(p => NativeCaravanCatalog.PawnEligible(p)
                && p.DevelopmentalStage.Adult()).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            if (pawns.Count == 0) return row;
            using var path = origin.Tile.Layer.Pather.FindPath(origin.Tile, target.Tile, null);
            row = row with { Reachable = path.Found };
            if (!path.Found) return row;
            var ticksPerMove = pawns.Max(p => CaravanTicksPerMoveUtility.GetTicksPerMove(new List<Pawn> { p }, 1, 1));
            row = row with { TravelTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(origin.Tile, target.Tile, path, 0, ticksPerMove, GenTicks.TicksAbs) };
            row = row with { RoutePawnIds = [.. row.RoutePawnIds, .. pawns.Select(p => p.GetUniqueLoadID())] };
            return row;
        }
    }
}
