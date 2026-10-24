#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>
/// The disease facts of the harm model's illness forecast (#3824), read without changing game state: never
/// IncidentWorker.CanFireNow nor BiomeDef.CommonalityOfDisease (it fills a lazy cache). The gates and the victim filter mirror IncidentWorker.CanFireNow and
/// IncidentWorker_Disease.PotentialVictims over public members; the brain applies the def-only gates from the catalog.
/// </summary>
internal static class NativeDiseaseFacts
{
    internal static Obs.DiseaseFacts Read(Map map)
    {
        var difficulty = Find.Storyteller.difficulty;
        var candidates = Candidates(map).ToList();
        var incidents = DiseaseIncidents().Select(def => new Obs.DiseaseIncidentRead(def.defName, candidates.Count(p => PotentialVictim(def, p)))
            { Blocked = [.. Blocked(map, def, difficulty)] });
        return new Obs.DiseaseFacts {
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
    internal static Obs.PawnDiseaseFacts? ReadPawn(Pawn pawn)
    {
        if (pawn.health?.immunity is not { } immunity) return null;
        var incidents = DiseaseIncidents().ToList();
        var capacities = pawn.health.capacities;
        return new Obs.PawnDiseaseFacts {
            ImmunityGainSpeed = pawn.GetStatValue(StatDefOf.ImmunityGainSpeed)
                / (pawn.InBed() && pawn.CurrentBed() is { } bed && bed.GetStatValue(StatDefOf.ImmunityGainSpeedFactor) is var factor and > 0 ? factor : 1f),
            Contracts = [.. incidents.Select(def => new Obs.DiseaseContract(def.defName,
                PotentialVictim(def, pawn) ? immunity.DiseaseContractChanceFactor(def.diseaseIncident) : 0))],
            BloodPumping = capacities.GetLevel(PawnCapacityDefOf.BloodPumping),
            Breathing = capacities.GetLevel(PawnCapacityDefOf.Breathing),
            BloodFiltration = capacities.GetLevel(PawnCapacityDefOf.BloodFiltration),
            Parts = [.. Parts(pawn, incidents)],
            Replacements = [.. Replacements(pawn, incidents)],
        };
    }

    // #3830: every part of a def an incident affects, and the incidents HediffGiverUtility.TryApply could give it to: not missing, not
    // already carrying the hediff, no directly added part on it or an ancestor.
    private static IEnumerable<Obs.DiseasePartRead> Parts(Pawn pawn, List<IncidentDef> incidents)
    {
        var set = pawn.health.hediffSet;
        var all = pawn.RaceProps.body.AllParts;
        for (var i = 0; i < all.Count; i++)
        {
            var part = all[i];
            var affecting = incidents.Where(d => d.diseasePartsToAffect != null && d.diseasePartsToAffect.Contains(part.def)).ToList();
            if (affecting.Count == 0) continue;
            var open = !set.PartIsMissing(part) && !set.PartOrAnyAncestorHasDirectlyAddedParts(part);
            yield return new Obs.DiseasePartRead(part.def.defName, part.coverageAbs, PawnCapacityUtility.CalculatePartEfficiency(set, part)) {
                Incidents = [.. affecting.Where(d => open && !set.HasHediff(d.diseaseIncident, part)).Select(d => d.defName)],
            };
        }
    }

    // #3830: per part def a HediffComp_DestroyOrgan disease affects, its first part record taken as missing, each install recipe
    // that would put it back (the surgery block's own recipe gates, NativePawnDetails.Surgery).
    private static IEnumerable<Obs.OrganReplacementRead> Replacements(Pawn pawn, List<IncidentDef> incidents)
    {
        var map = pawn.MapHeld;
        if (pawn.Dead || map == null) yield break;
        var defs = incidents.Where(d => d.diseaseIncident.comps != null && d.diseaseIncident.comps.Any(c => c is HediffCompProperties_DestroyOrgan))
            .SelectMany(d => d.diseasePartsToAffect ?? new List<BodyPartDef>()).Distinct().OrderBy(d => d.defName, System.StringComparer.Ordinal);
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
    // and ScenPart_DisableIncident.
    private static IEnumerable<string> Blocked(Map map, IncidentDef def, Difficulty difficulty)
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
