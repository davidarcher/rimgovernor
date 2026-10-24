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
    internal static Obs.PawnDiseaseFacts? ReadPawn(Pawn pawn) =>
        pawn.health?.immunity is not { } immunity ? null : new Obs.PawnDiseaseFacts {
            ImmunityGainSpeed = pawn.GetStatValue(StatDefOf.ImmunityGainSpeed)
                / (pawn.InBed() && pawn.CurrentBed() is { } bed && bed.GetStatValue(StatDefOf.ImmunityGainSpeedFactor) is var factor and > 0 ? factor : 1f),
            Contracts = [.. DiseaseIncidents().Select(def => new Obs.DiseaseContract(def.defName,
                PotentialVictim(def, pawn) ? immunity.DiseaseContractChanceFactor(def.diseaseIncident) : 0))],
        };

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
