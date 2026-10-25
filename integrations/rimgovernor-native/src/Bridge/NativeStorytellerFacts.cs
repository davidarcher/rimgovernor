#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;


namespace RimGovernor.Native;

/// <summary>
/// The colony facts the storyteller's comps read that the threat, disease and trader schedules share (#3860), read without changing
/// game state: StorytellerUtility.GetProgressScore (an OnOffCycle's acceptPercentFactorPerProgressScoreCurve), the tree-connection
/// check StorytellerComp_OnOffCycle makes before swapping to its onDaysNoTreeConnectors, and the incidents a ThreatsGenerator draws.
/// </summary>
internal static class NativeStorytellerFacts
{
    /// <summary>
    /// CategoryIndividualMTBByBiome.GenerateParms delegates to DefaultParmsNow. Use the worker itself so base and worker-specific
    /// gates have one owner, including custom workers. CanFireNow maintains its normal per-tick cache; detached parms and an
    /// isolated random state keep observation from altering the generated incident or consuming the game's random stream.
    /// </summary>
    internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.ThreatBiomeIncidentRead> BiomeIncidents(Map map)
    {
        foreach (var incident in DefDatabase<IncidentDef>.AllDefsListForReading
            .Where(d => d.category == IncidentCategoryDefOf.ThreatBig && d.mtbDaysByBiome != null
                && d.targetTags != null && d.targetTags.Contains(IncidentTargetTagDefOf.Map_PlayerHome))
            .OrderBy(d => d.defName, System.StringComparer.Ordinal))
        {
            bool canFire;
            Rand.PushState();
            try { canFire = incident.Worker.CanFireNow(StorytellerUtility.DefaultParmsNow(incident.category, map)); }
            finally { Rand.PopState(); }
            yield return new RimGovernor.Brain.GameFacts.Observations.ThreatBiomeIncidentRead(incident.defName, canFire);
        }
    }

    internal static double ProgressScore(Map map) => StorytellerUtility.GetProgressScore(map);

    /// <summary>StorytellerComp_OnOffCycle: the player faction tracks ideoligions and none of them has the TreeConnection meme.</summary>
    internal static bool NoTreeConnection() =>
        Faction.OfPlayer.ideos != null && !Faction.OfPlayer.ideos.AllIdeos.Any(ideo => ideo.HasMeme(MemeDefOf.TreeConnection));

    /// <summary>
    /// ThreatsGenerator.GetPossibleIncidents, RaidEnemy then MechCluster (MechCluster only with Royalty, where its def exists), each with
    /// its BaseChanceThisGame and whether its worker would fire for the forced parms ThreatsGenerator.MakeThreat passes at the map's
    /// own points: IncidentWorker.CanFireNow past its gates is CanFireNowSub (and false in an active void awakening).
    /// </summary>
    internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.ThreatIncidentRead> GeneratorIncidents(Map map)
    {
        for (var index = 0; index < Find.Storyteller.def.comps.Count; index++)
        {
            if (Find.Storyteller.def.comps[index] is not StorytellerCompProperties_ThreatsGenerator { parms: { } generator }) continue;
            foreach (var incident in GeneratorIncidents(map, generator)) yield return incident with { CompIndex = index };
        }
    }

    private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.ThreatIncidentRead> GeneratorIncidents(Map map, ThreatsGeneratorParams generator)
    {
        var voidAwakening = ModsConfig.AnomalyActive && Find.Anomaly.VoidAwakeningActive();
        var parms = new IncidentParms { target = map, points = generator.threatPoints ?? StorytellerUtility.DefaultThreatPointsNow(map) * generator.currentThreatPointsFactor, forced = true, faction = generator.faction };
        if (generator.minThreatPoints is { } minimum) parms.points = System.Math.Max(parms.points, minimum);
        var raid = IncidentDefOf.RaidEnemy;
        // IncidentWorker_PawnsArrive.CanFireNowSub with no faction in the parms: some faction can be the raid's source.
        var raidWorker = (IncidentWorker_PawnsArrive)raid.Worker;
        yield return new RimGovernor.Brain.GameFacts.Observations.ThreatIncidentRead(raid.defName, raidWorker.BaseChanceThisGame,
            !voidAwakening && Find.FactionManager.AllFactions.Any(f => raidWorker.FactionCanBeGroupSource(f, parms)));
        if (ModsConfig.RoyaltyActive && IncidentDefOf.MechCluster is { } cluster)
        {
            // IncidentWorker_MechCluster.CanFireNowSub: Royalty and a mechanoid faction.
            yield return new RimGovernor.Brain.GameFacts.Observations.ThreatIncidentRead(cluster.defName, cluster.Worker.BaseChanceThisGame, !voidAwakening && Faction.OfMechanoids != null);
        }
        else
        {
            yield return new RimGovernor.Brain.GameFacts.Observations.ThreatIncidentRead(nameof(IncidentDefOf.MechCluster), 0, false);
        }
    }
}
