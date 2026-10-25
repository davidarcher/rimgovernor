using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestWorkers
    {
        internal static IEnumerable<Obs.QuestWorker> Read(Map map)
        {
            var stats = DefDatabase<RecipeDef>.AllDefsListForReading.Select(r => r.workSpeedStat)
                .Where(s => s != null).Concat(new[] { StatDefOf.ConstructionSpeed, StatDefOf.PlantWorkSpeed }).Distinct()
                .OrderBy(s => s.defName, StringComparer.Ordinal).ToArray();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                // The health predicate is CompShuttle.PawnIsHealthyEnoughForShuttle;
                // adulthood is the shuttle's acceptChildren=false predicate.
                yield return new Obs.QuestWorker
                {
                    PawnId = new PawnId(pawn.GetUniqueLoadID()),
                    HealthyAdult = !pawn.Dead && !pawn.Downed && !pawn.InMentalState && pawn.DevelopmentalStage.Adult()
                        && pawn.health.capacities.CanBeAwake && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                        && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation),
                    CanFight = !pawn.Dead && !pawn.Downed && !pawn.InMentalState && !pawn.WorkTagIsDisabled(WorkTags.Violent),
                    CarryCapacity = MassUtility.Capacity(pawn),
                    CarriedMass = MassUtility.GearAndInventoryMass(pawn),
                    FactionLeader = pawn == Faction.OfPlayer.leader,
                    Negotiation = Negotiation(pawn),
                    Rates = [.. stats.Select(stat => new Obs.QuestWorkRate { Stat = stat.defName,
                        Rate = stat.Worker.IsDisabledFor(pawn) ? 0 : pawn.GetStatValue(stat) })],
                };
            }
        }

        private static Obs.QuestNegotiation Negotiation(Pawn pawn)
        {
            if (StatDefOf.NegotiationAbility.Worker.IsDisabledFor(pawn)) return new Obs.QuestNegotiation.Incapable();
            var skills = pawn.skills ?? throw new InvalidOperationException($"Free colonist {pawn.GetUniqueLoadID()} has no skills to read Social from.");
            return new Obs.QuestNegotiation.Able(pawn.GetStatValue(StatDefOf.NegotiationAbility), skills.GetSkill(SkillDefOf.Social).Level);
        }
    }
}
