using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using MapId = RimGovernor.Brain.Domain.MapId;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using QuestId = RimGovernor.Brain.Domain.QuestId;

namespace RimGovernor.Native
{
    internal static class NativeWorldSites
    {
        // Every row is whole: a site this read cannot place or route throws, failing the world progression read.
        internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.WorldSite> Read(RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var quests = Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI && !q.Historical && !q.dismissed).ToArray();
            var targets = quests.SelectMany(q => q.QuestLookTargets).Where(t => t.HasWorldObject).Select(t => t.WorldObject).Distinct().ToArray();
            var origin = Find.Maps.FirstOrDefault(m => context.Identity?.MapId is not null && m.uniqueID == context.Identity.MapId)
                ?? throw new InvalidOperationException("World sites need the observed colony map to route from.");
            foreach (var world in Find.WorldObjects.AllWorldObjects.OfType<Site>().Cast<WorldObject>().Concat(targets).Distinct().OrderBy(w => w.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var site = world as Site;
                RimGovernor.Brain.GameFacts.Observations.QuestPeaceTalks? talks = null;
                if (world is PeaceTalks && world.Faction != null)
                {
                    var goodwill = Faction.OfPlayer.GoodwillWith(world.Faction);
                    talks = new RimGovernor.Brain.GameFacts.Observations.QuestPeaceTalks {
                        WorstGoodwillLoss = -System.Math.Min(DiplomacyTuning.Goodwill_PeaceTalksDisasterRange.min, Faction.OfPlayer.GoodwillToMakeHostile(world.Faction)),
                        BestGoodwillGain = System.Math.Min(DiplomacyTuning.Goodwill_PeaceTalksTriumphRange.max, DiplomacyTuning.MaxGoodwill - goodwill), IdeologyActive = ModsConfig.IdeologyActive };
                }
                yield return new RimGovernor.Brain.GameFacts.Observations.WorldSite
                {
                    Id = world.GetUniqueLoadID(),
                    State = world.Destroyed ? RimGovernor.Brain.GameFacts.Observations.WorldSiteState.Destroyed : world.Spawned ? RimGovernor.Brain.GameFacts.Observations.WorldSiteState.Spawned : RimGovernor.Brain.GameFacts.Observations.WorldSiteState.Pending,
                    Place = Place(world, site),
                    QuestIds = [.. quests.Where(q => q.QuestLookTargets.Any(t => t.HasWorldObject && t.WorldObject == world)).Select(q => new QuestId(q.GetUniqueLoadID()))],
                    Route = Route(origin, world),
                    Security = site != null ? NativeSiteSecurity.Budget(site, quests) : new RimGovernor.Brain.GameFacts.Observations.SiteThreatBudget.NotASite(),
                    PeaceTalks = talks,
                };
            }
        }

        private static RimGovernor.Brain.GameFacts.Observations.SitePlace Place(WorldObject world, Site? site)
        {
            var map = (world as MapParent)?.HasMap == true ? ((MapParent)world).Map : null;
            if (!world.Tile.Valid)
            {
                if (map != null) throw new InvalidOperationException("World object " + world.GetUniqueLoadID() + " has a map but no world tile.");
                return new RimGovernor.Brain.GameFacts.Observations.SitePlace.Unplaced();
            }
            if (map == null) return new RimGovernor.Brain.GameFacts.Observations.SitePlace.OnTile(world.Tile.tileId, world.Tile.Layer.LayerID);
            return new RimGovernor.Brain.GameFacts.Observations.SitePlace.Loaded(world.Tile.tileId, world.Tile.Layer.LayerID, new RimGovernor.Brain.GameFacts.Observations.SiteMap
            {
                Id = new MapId(map.uniqueID),
                Threat = NativeSiteSecurity.Threat(map),
                Extraction = NativeSiteExtraction.Read(map),
                MiningTargets = site != null ? [.. NativeQuestSiteMining.Read(site, map)] : [],
            });
        }

        // A full-mass, slowest-singleton estimate bounds any human-only subset
        // of this observed crew, without assuming an averaged caravan bonus.
        private static RimGovernor.Brain.GameFacts.Observations.SiteRoute Route(Map origin, WorldObject target)
        {
            if (!target.Tile.Valid) return new RimGovernor.Brain.GameFacts.Observations.SiteRoute.Unreachable();
            if (!target.Tile.Layer.IsRootSurface || !origin.Tile.Layer.IsRootSurface) return new RimGovernor.Brain.GameFacts.Observations.SiteRoute.OffSurface();
            var pawns = origin.mapPawns.FreeColonistsSpawned.Where(p => NativeCaravanCatalog.PawnEligible(p)
                && p.DevelopmentalStage.Adult()).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            if (pawns.Count == 0) return new RimGovernor.Brain.GameFacts.Observations.SiteRoute.NoCrew();
            using var path = origin.Tile.Layer.Pather.FindPath(origin.Tile, target.Tile, null);
            if (!path.Found) return new RimGovernor.Brain.GameFacts.Observations.SiteRoute.Unreachable();
            var ticksPerMove = pawns.Max(p => CaravanTicksPerMoveUtility.GetTicksPerMove(new List<Pawn> { p }, 1, 1));
            return new RimGovernor.Brain.GameFacts.Observations.SiteRoute.Reachable(CaravanArrivalTimeEstimator.EstimatedTicksToArrive(origin.Tile, target.Tile, path, 0, ticksPerMove, GenTicks.TicksAbs),
                [.. pawns.Select(p => new PawnId(p.GetUniqueLoadID()))]);
        }
    }
}
