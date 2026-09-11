using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    /// Native route evidence and supervised hunting checks. No game orders.
    internal static class HuntingSafety
    {
        internal static bool RouteSafe(Pawn hunter, Pawn prey)
        {
            if (hunter == null || prey == null || !hunter.Spawned || !prey.Spawned
                || hunter.Map != prey.Map || prey.IsForbidden(hunter)
                || prey.RaceProps.DeathActionWorker?.GetType() != typeof(DeathActionWorker_Simple)
                || !hunter.CanReach(prey, PathEndMode.Touch, Danger.None)) return false;
            var map = hunter.Map;
            var predators = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead
                && p.Faction != Faction.OfPlayer && p.RaceProps.predator).ToList();
            using (var path = map.pathFinder.FindPathNow(hunter.Position, prey,
                TraverseParms.For(hunter, Danger.None), peMode: PathEndMode.Touch))
            {
                return path.Found && path.NodesReversed.All(cell => !predators.Any(p =>
                    Math.Max(Math.Abs(p.Position.x - cell.x), Math.Abs(p.Position.z - cell.z)) <= 25));
            }
        }

        internal static object Read(Pawn prey)
        {
            try
            {
                var hunters = new List<string>();
                if (prey.Spawned && !prey.Dead && !prey.Downed && prey.Faction == null
                    && prey.RaceProps.Animal && !prey.RaceProps.predator
                    && prey.RaceProps.meatDef?.IsNutritionGivingIngestible == true
                    && prey.RaceProps.manhunterOnDamageChance == 0)
                    foreach (var pawn in prey.Map.mapPawns.FreeColonistsSpawned)
                        if (!pawn.Downed && !pawn.Drafted && !pawn.InMentalState
                            && pawn.workSettings != null && pawn.workSettings.WorkIsActive(WorkTypeDefOf.Hunting)
                            && pawn.equipment?.Primary?.def.IsRangedWeapon == true
                            && pawn.Position.DistanceToSquared(prey.Position) <= 10000
                            && RouteSafe(pawn, prey)) hunters.Add(pawn.GetUniqueLoadID());
                return new { readable = true, hunters, meatDef = prey.RaceProps.meatDef?.defName,
                    deathAction = prey.RaceProps.DeathActionWorker?.GetType().FullName,
                    scope = "Ordinary native death action and current Danger.None touch route avoiding wild predators by 25 cells; future movement and shooting positions can change." };
            }
            catch (Exception error)
            {
                return new { readable = false, error = error.GetType().Name };
            }
        }
    }
}
