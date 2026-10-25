using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using MapId = RimGovernor.Brain.Domain.MapId;
using Obs = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using QuestId = RimGovernor.Brain.Domain.QuestId;

namespace HomeBridge.BridgeTools
{
    internal static class NativeWorldSites
    {
        // Every row is whole: a site this read cannot place or route throws, failing the world progression read.
        internal static IEnumerable<Obs.WorldSite> Read(Common.ObservationContext context)
        {
            var quests = Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI && !q.Historical && !q.dismissed).ToArray();
            var targets = quests.SelectMany(q => q.QuestLookTargets).Where(t => t.HasWorldObject).Select(t => t.WorldObject).Distinct().ToArray();
            var origin = Find.Maps.FirstOrDefault(m => context.Identity?.MapId is not null && m.uniqueID == context.Identity.MapId)
                ?? throw new InvalidOperationException("World sites need the observed colony map to route from.");
            foreach (var world in Find.WorldObjects.AllWorldObjects.OfType<Site>().Cast<WorldObject>().Concat(targets).Distinct().OrderBy(w => w.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var site = world as Site;
                Obs.QuestPeaceTalks? talks = null;
                if (world is PeaceTalks && world.Faction != null)
                {
                    var goodwill = Faction.OfPlayer.GoodwillWith(world.Faction);
                    talks = new Obs.QuestPeaceTalks {
                        WorstGoodwillLoss = -System.Math.Min(DiplomacyTuning.Goodwill_PeaceTalksDisasterRange.min, Faction.OfPlayer.GoodwillToMakeHostile(world.Faction)),
                        BestGoodwillGain = System.Math.Min(DiplomacyTuning.Goodwill_PeaceTalksTriumphRange.max, DiplomacyTuning.MaxGoodwill - goodwill), IdeologyActive = ModsConfig.IdeologyActive };
                }
                yield return new Obs.WorldSite
                {
                    Id = world.GetUniqueLoadID(),
                    State = world.Destroyed ? Obs.WorldSiteState.Destroyed : world.Spawned ? Obs.WorldSiteState.Spawned : Obs.WorldSiteState.Pending,
                    Place = Place(world, site),
                    QuestIds = [.. quests.Where(q => q.QuestLookTargets.Any(t => t.HasWorldObject && t.WorldObject == world)).Select(q => new QuestId(q.GetUniqueLoadID()))],
                    Route = Route(origin, world),
                    Security = site != null ? NativeSiteSecurity.Budget(site, quests) : new Obs.SiteThreatBudget.NotASite(),
                    PeaceTalks = talks,
                };
            }
        }

        private static Obs.SitePlace Place(WorldObject world, Site? site)
        {
            var map = (world as MapParent)?.HasMap == true ? ((MapParent)world).Map : null;
            if (!world.Tile.Valid)
            {
                if (map != null) throw new InvalidOperationException("World object " + world.GetUniqueLoadID() + " has a map but no world tile.");
                return new Obs.SitePlace.Unplaced();
            }
            if (map == null) return new Obs.SitePlace.OnTile(world.Tile.tileId, world.Tile.Layer.LayerID);
            return new Obs.SitePlace.Loaded(world.Tile.tileId, world.Tile.Layer.LayerID, new Obs.SiteMap
            {
                Id = new MapId(map.uniqueID),
                Threat = NativeSiteSecurity.Threat(map),
                Extraction = NativeSiteExtraction.Read(map),
                MiningTargets = site != null ? [.. NativeQuestSiteMining.Read(site, map)] : [],
            });
        }

        // A full-mass, slowest-singleton estimate bounds any human-only subset
        // of this observed crew, without assuming an averaged caravan bonus.
        private static Obs.SiteRoute Route(Map origin, WorldObject target)
        {
            if (!target.Tile.Valid) return new Obs.SiteRoute.Unreachable();
            if (!target.Tile.Layer.IsRootSurface || !origin.Tile.Layer.IsRootSurface) return new Obs.SiteRoute.OffSurface();
            var pawns = origin.mapPawns.FreeColonistsSpawned.Where(p => NativeCaravanCatalog.PawnEligible(p)
                && p.DevelopmentalStage.Adult()).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            if (pawns.Count == 0) return new Obs.SiteRoute.NoCrew();
            using var path = origin.Tile.Layer.Pather.FindPath(origin.Tile, target.Tile, null);
            if (!path.Found) return new Obs.SiteRoute.Unreachable();
            var ticksPerMove = pawns.Max(p => CaravanTicksPerMoveUtility.GetTicksPerMove(new List<Pawn> { p }, 1, 1));
            return new Obs.SiteRoute.Reachable(CaravanArrivalTimeEstimator.EstimatedTicksToArrive(origin.Tile, target.Tile, path, 0, ticksPerMove, GenTicks.TicksAbs),
                [.. pawns.Select(p => new PawnId(p.GetUniqueLoadID()))]);
        }
    }
}
