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
    internal static RimGovernor.Brain.GameFacts.Observations.IncidentClockRead IncidentClock(Map map, double horizon,
        IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.IncidentCategoryRead> categories) =>
        NativeIncidentClockReader.Read(map, horizon, categories);
    internal static IEnumerable<RimGovernor.Brain.GameFacts.Observations.IncidentCycleTimes> Cycles(Map map, double horizon)
    {
        var points = StorytellerUtility.DefaultThreatPointsNow(map);
        var score = StorytellerUtility.GetProgressScore(map);
        var noTree = NoTreeConnection();
        var worldSeed = Find.World.info.persistentRandomValue;
        var targetSeed = ((IIncidentTarget)map).ConstantRandSeed;
        var now = Find.TickManager.TicksSinceSettle;
        for (var index = 0; index < Find.Storyteller.def.comps.Count; index++)
        {
            var comp = Find.Storyteller.def.comps[index];
            var liveIndex = Find.Storyteller.storytellerComps.FindIndex(c => c.props == comp);
            if (liveIndex < 0) continue;
            var targetTags = map.IncidentTargetTags().ToArray();
            if (targetTags.Any(tag => comp.disallowedTargetTags?.Contains(tag) == true)
                || comp.allowedTargetTags is { Count: > 0 } tags && !targetTags.Any(tags.Contains)) continue;
            IncidentCycleReader.Parameters? input = null;
            if (comp.GetType() == typeof(StorytellerCompProperties_OnOffCycle) && comp is StorytellerCompProperties_OnOffCycle cycle)
            {
                var factor = (cycle.acceptPercentFactorPerThreatPointsCurve?.Evaluate(points) ?? 1)
                    * (cycle.acceptPercentFactorPerProgressScoreCurve?.Evaluate(score) ?? 1);
                input = new()
                {
                    CompIndex = index, Salt = liveIndex, StartDays = cycle.minDaysPassed, MinDaysPassed = cycle.minDaysPassed,
                    OnDays = noTree && cycle.onDaysNoTreeConnectors != 0 ? cycle.onDaysNoTreeConnectors : cycle.onDays,
                    OffDays = noTree && cycle.offDaysNoTreeConnectors != 0 ? cycle.offDaysNoTreeConnectors : cycle.offDays,
                    SpacingDays = cycle.minSpacingDays, Min = cycle.numIncidentsRange.min, Max = cycle.numIncidentsRange.max,
                    Accept = factor,
                    AcceptAtDay = cycle.acceptFractionByDaysPassedCurve is { } curve ? day => factor * curve.Evaluate((float)day) : null,
                };
            }
            else if (comp.GetType() == typeof(StorytellerCompProperties_ThreatsGenerator) && comp is StorytellerCompProperties_ThreatsGenerator { parms: { } generator })
            {
                var count = IncidentCycleReader.GeneratorCountFactor(Find.Storyteller.difficulty.threatScale);
                input = new()
                {
                    CompIndex = index, Salt = generator.randSeed, StartDays = GenDate.TickGameToSettled(map.generationTick) / 60000f, MinDaysPassed = comp.minDaysPassed,
                    OnDays = generator.onDays, OffDays = generator.offDays, SpacingDays = generator.minSpacingDays,
                    Min = generator.numIncidentsRange.min * (float)count, Max = generator.numIncidentsRange.max * (float)count,
                };
            }
            else if (comp.GetType() == typeof(StorytellerCompProperties_FactionInteraction) && comp is StorytellerCompProperties_FactionInteraction interaction)
            {
                var fraction = StorytellerUtility.AllyIncidentFraction(interaction.fullAlliesOnly);
                if (fraction <= 0 || interaction.minWealth > 0 && map.wealthWatcher.WealthTotal < interaction.minWealth
                    || interaction.minDanger != StoryDanger.None && (int)map.dangerWatcher.DangerRating < (int)interaction.minDanger)
                {
                    yield return new(index, horizon, []);
                    continue;
                }
                input = new()
                {
                    CompIndex = index, Salt = liveIndex, StartDays = interaction.minDaysPassed, MinDaysPassed = interaction.minDaysPassed,
                    OnDays = GenDate.DaysPerYear, OffDays = 0, SpacingDays = interaction.minSpacingDays,
                    Min = interaction.baseIncidentsPerYear, Max = interaction.baseIncidentsPerYear, Accept = fraction,
                };
            }
            if (input is not null) yield return IncidentCycleReader.Read(input, worldSeed, targetSeed, now, horizon);
        }
    }

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
