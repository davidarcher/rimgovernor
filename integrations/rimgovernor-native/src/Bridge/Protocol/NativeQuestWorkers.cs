#nullable enable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestWorkers
    {
        internal static IEnumerable<Obs.QuestWorker> Read(Map map)
        {
            var stats = DefDatabase<RecipeDef>.AllDefsListForReading.Select(r => r.workSpeedStat)
                .Where(s => s != null).Concat(new[] { StatDefOf.ConstructionSpeed, StatDefOf.PlantWorkSpeed }).Distinct()
                .OrderBy(s => s.defName, System.StringComparer.Ordinal).ToArray();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                // The health predicate is CompShuttle.PawnIsHealthyEnoughForShuttle;
                // adulthood is the shuttle's acceptChildren=false predicate.
                var row = new Obs.QuestWorker { PawnId = pawn.GetUniqueLoadID(),
                    HealthyAdult = !pawn.Dead && !pawn.Downed && !pawn.InMentalState && pawn.DevelopmentalStage.Adult()
                        && pawn.health.capacities.CanBeAwake && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                        && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation),
                    CanFight = !pawn.Dead && !pawn.Downed && !pawn.InMentalState && !pawn.WorkTagIsDisabled(WorkTags.Violent),
                    CarryCapacity = MassUtility.Capacity(pawn), CarriedMass = MassUtility.GearAndInventoryMass(pawn) };
                row.FactionLeader = pawn == Faction.OfPlayer.leader;
                if (!StatDefOf.NegotiationAbility.Worker.IsDisabledFor(pawn))
                {
                    row.NegotiationAbility = pawn.GetStatValue(StatDefOf.NegotiationAbility);
                    if (pawn.skills != null) row.SocialLevel = pawn.skills.GetSkill(SkillDefOf.Social).Level;
                }
                foreach (var stat in stats)
                    row.Rates.Add(new Obs.QuestWorkRate { Stat = stat.defName,
                        Rate = stat.Worker.IsDisabledFor(pawn) ? 0 : pawn.GetStatValue(stat) });
                yield return row;
            }
        }
    }
}
