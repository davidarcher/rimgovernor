using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>
/// The disease facts of the harm model's illness forecast (#3824), read without changing game state: never
/// IncidentWorker.CanFireNow nor BiomeDef.CommonalityOfDisease (it fills a lazy cache). The gates and the victim filter mirror IncidentWorker.CanFireNow and
/// IncidentWorker_Disease.PotentialVictims over public members; the brain applies the def-only gates from the catalog.
/// </summary>
internal static class NativeDiseaseFacts
{
    internal static RimGovernor.Brain.GameFacts.Observations.DiseaseFacts Read(Map map)
    {
        var difficulty = Find.Storyteller.difficulty;
        var candidates = Candidates(map).ToList();
        var incidents = DiseaseIncidents().Select(def => new RimGovernor.Brain.GameFacts.Observations.DiseaseIncidentRead(def.defName, candidates.Count(p => PotentialVictim(def, p)))
            { Blocked = [.. Blocked(map, def, difficulty)] });
        return new RimGovernor.Brain.GameFacts.Observations.DiseaseFacts {
            Storyteller = Find.Storyteller.def.defName, Biome = Find.WorldGrid[map.Tile].PrimaryBiome.defName,
            RandomDiseases = DebugSettings.enableRandomDiseases, IntervalFactor = difficulty.diseaseIntervalFactor,
            DaysSinceSettle = GenDate.DaysPassedSinceSettleFloat, Candidates = candidates.Count,
            FreeColonists = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count(),
            Incidents = [.. incidents],
        };
    }

    /// <summary>
    /// One pawn's ImmunityGainSpeed without its bed (StatPart_BedStat multiplies CurrentBed's ImmunityGainSpeedFactor while it lies in
    /// one; it is divided out, #3804) and the chance each DiseaseHuman incident's hediff takes hold (0 when it is no potential victim).
    /// </summary>
    internal static RimGovernor.Brain.GameFacts.Observations.PawnDiseaseFacts? ReadPawn(Pawn pawn)
    {
        if (pawn.health?.immunity is not { } immunity) return null;
        var incidents = DiseaseIncidents().ToList();
        var triggered = TriggeredHediffs(pawn);
        var capacities = pawn.health.capacities;
        return new RimGovernor.Brain.GameFacts.Observations.PawnDiseaseFacts {
            ImmunityGainSpeed = pawn.GetStatValue(StatDefOf.ImmunityGainSpeed)
                / (pawn.InBed() && pawn.CurrentBed() is { } bed && bed.GetStatValue(StatDefOf.ImmunityGainSpeedFactor) is var factor and > 0 ? factor : 1f),
            Contracts = [.. incidents.Select(def => new RimGovernor.Brain.GameFacts.Observations.DiseaseContract(def.defName,
                PotentialVictim(def, pawn) ? immunity.DiseaseContractChanceFactor(def.diseaseIncident) : 0))],
            BloodPumping = capacities.GetLevel(PawnCapacityDefOf.BloodPumping),
            Breathing = capacities.GetLevel(PawnCapacityDefOf.Breathing),
            BloodFiltration = capacities.GetLevel(PawnCapacityDefOf.BloodFiltration),
            Parts = [.. Parts(pawn, incidents, triggered)],
            Replacements = [.. Replacements(pawn, incidents, triggered)],
        };
    }

    // #3844: on a creepjoiner, the hediffs its downsides' manually triggered ReplaceHediff comps give to parts
    // (HediffComp_ReplaceHediff.Trigger, which DoDownside calls): every CreepJoinerDownsideDef's, since which downside the pawn has
    // is hidden. None for another pawn or without Anomaly.
    private static List<HediffCompProperties_ReplaceHediff.TriggeredHediff> TriggeredHediffs(Pawn pawn)
    {
        if (!ModsConfig.AnomalyActive || pawn.creepjoiner == null) return new List<HediffCompProperties_ReplaceHediff.TriggeredHediff>();
        return DefDatabase<CreepJoinerDownsideDef>.AllDefsListForReading.Where(d => d.hediffs != null).SelectMany(d => d.hediffs)
            .Where(h => h?.comps != null).SelectMany(h => h.comps.OfType<HediffCompProperties_ReplaceHediff>())
            .Where(r => r.manuallyTriggered && r.hediffs != null).SelectMany(r => r.hediffs)
            .Where(t => t?.hediff != null && t.partsToAffect != null).ToList();
    }

    // #3830: every part of a def an incident affects, and the incidents HediffGiverUtility.TryApply could give it to: not missing, not
    // already carrying the hediff, no directly added part on it or an ancestor. #3844: a creepjoiner's parts of a def a triggered
    // hediff affects too, with the triggered hediffs TryApply could give it by the same test.
    private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.DiseasePartRead> Parts(Pawn pawn, List<IncidentDef> incidents, List<HediffCompProperties_ReplaceHediff.TriggeredHediff> triggered)
    {
        var set = pawn.health.hediffSet;
        var all = pawn.RaceProps.body.AllParts;
        for (var i = 0; i < all.Count; i++)
        {
            var part = all[i];
            var affecting = incidents.Where(d => d.diseasePartsToAffect != null && d.diseasePartsToAffect.Contains(part.def)).ToList();
            var giving = triggered.Where(t => t.partsToAffect.Contains(part.def)).Select(t => t.hediff).Distinct().ToList();
            if (affecting.Count == 0 && giving.Count == 0) continue;
            var open = !set.PartIsMissing(part) && !set.PartOrAnyAncestorHasDirectlyAddedParts(part);
            yield return new RimGovernor.Brain.GameFacts.Observations.DiseasePartRead(part.def.defName, part.coverageAbs, PawnCapacityUtility.CalculatePartEfficiency(set, part)) {
                Incidents = [.. affecting.Where(d => open && !set.HasHediff(d.diseaseIncident, part)).Select(d => d.defName)],
                Hediffs = [.. giving.Where(h => open && !set.HasHediff(h, part)).Select(h => h.defName).OrderBy(h => h, System.StringComparer.Ordinal)],
            };
        }
    }

    // #3830: per part def a HediffComp_DestroyOrgan disease affects, its first part record taken as missing, each install recipe
    // that would put it back (the surgery block's own recipe gates, NativePawnDetails.Surgery). #3844: and per part def a
    // creepjoiner's DestroyOrgan triggered hediff affects.
    private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.OrganReplacementRead> Replacements(Pawn pawn, List<IncidentDef> incidents, List<HediffCompProperties_ReplaceHediff.TriggeredHediff> triggered)
    {
        var map = pawn.MapHeld;
        if (pawn.Dead || map == null) yield break;
        var defs = incidents.Where(d => d.diseaseIncident.comps != null && d.diseaseIncident.comps.Any(c => c is HediffCompProperties_DestroyOrgan))
            .SelectMany(d => d.diseasePartsToAffect ?? new List<BodyPartDef>())
            .Concat(triggered.Where(t => t.hediff.comps != null && t.hediff.comps.Any(c => c is HediffCompProperties_DestroyOrgan)).SelectMany(t => t.partsToAffect))
            .Distinct().OrderBy(d => d.defName, System.StringComparer.Ordinal);
        var facts = NativePawnDetails.SurgeryScope.Facts(map);
        foreach (var def in defs)
        {
            var part = pawn.RaceProps.body.AllParts.FirstOrDefault(p => p.def == def);
            if (part == null) continue;
            foreach (var recipe in pawn.def.AllRecipes.Where(r => r.AvailableNow && r.Worker is Recipe_InstallNaturalBodyPart or Recipe_InstallArtificialBodyPart
                && r.appliedOnFixedBodyParts != null && r.appliedOnFixedBodyParts.Contains(def) && r.Worker.AvailableReport(pawn).Accepted && r.AvailableOnNow(pawn, part))
                .OrderBy(r => r.defName, System.StringComparer.Ordinal))
                yield return NativePawnDetails.Replacement(pawn, recipe, part, facts, map);
        }
    }

    private static IEnumerable<IncidentDef> DiseaseIncidents() =>
        DefDatabase<IncidentDef>.AllDefsListForReading.Where(d => d.category == IncidentCategoryDefOf.DiseaseHuman && d.diseaseIncident != null)
            .OrderBy(d => d.defName, System.StringComparer.Ordinal);

    // IncidentWorker_DiseaseHuman.PotentialVictimCandidates on a map.
    private static IEnumerable<Pawn> Candidates(Map map) =>
        map.mapPawns.FreeColonistsAndPrisoners.Where(p => p.ParentHolder is not CompBiosculpterPod);

    // The game-state gates of IncidentWorker.CanFireNow the catalog cannot see: the difficulty's disabledWhen, the planet layer lists
    // and ScenPart_DisableIncident (the trader incidents read them too, NativeTradeVisitFacts).
    internal static IEnumerable<string> Blocked(Map map, IncidentDef def, Difficulty difficulty)
    {
        if (Find.Scenario.AllParts.Any(p => p is ScenPart_DisableIncident disable && disable.Incident == def)) yield return "scenario";
        if (!difficulty.AllowedBy(def.disabledWhen)) yield return "difficulty";
        if (map.Tile.Valid)
        {
            var layer = map.Tile.LayerDef;
            if (!def.layerWhitelist.NullOrEmpty() && !def.layerWhitelist.Contains(layer) || !def.layerBlacklist.NullOrEmpty() && def.layerBlacklist.Contains(layer)
                || !def.canOccurOnAllPlanetLayers && layer.onlyAllowWhitelistedIncidents && (def.layerWhitelist.NullOrEmpty() || !def.layerWhitelist.Contains(layer)))
                yield return "planet_layer";
        }
    }

    // IncidentWorker_Disease.PotentialVictims, one pawn: not in cryptosleep, no dryad, the developmental stage, a free part of an
    // affected def (CanAddHediffToAnyPartOfDef), flesh.
    private static bool PotentialVictim(IncidentDef def, Pawn pawn)
    {
        if (pawn.ParentHolder is Building_CryptosleepCasket || pawn.RaceProps.Dryad || !def.diseaseDevelopmentStage.Has(pawn.DevelopmentalStage)) return false;
        if (!def.diseasePartsToAffect.NullOrEmpty() && !def.diseasePartsToAffect.Any(part => CanAddToPartOfDef(pawn, def.diseaseIncident, part))) return false;
        return pawn.RaceProps.IsFlesh;
    }

    private static bool CanAddToPartOfDef(Pawn pawn, HediffDef hediff, BodyPartDef partDef)
    {
        if (pawn.ageTracker.CurLifeStage == LifeStageDefOf.HumanlikeBaby && Find.Storyteller.difficulty.babiesAreHealthy) return false;
        if (!hediff.canAffectBionicOrImplant && pawn.health.hediffSet.IsBionicOrImplant(partDef)) return false;
        return pawn.def.race.body.AllParts.Any(record => record.def == partDef && !pawn.health.hediffSet.PartIsMissing(record) && !pawn.health.hediffSet.HasHediff(hediff, record));
    }
}
