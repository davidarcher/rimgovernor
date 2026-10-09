#nullable enable

using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    /// Native route evidence and supervised hunting checks. The only game
    /// order is Withdraw, the supervisor's reaction to an unsafe hunt.
    internal static class HuntingSafety
    {
        /// Cancels an unsafe hunt: removes the prey's Hunt designation and
        /// ends the hunter's job so nobody re-takes it. True when anything
        /// was withdrawn.
        internal static bool Withdraw(Pawn hunter, Pawn prey)
        {
            var withdrawn = false;
            if (prey.Spawned)
            {
                var designation = prey.Map.designationManager.DesignationOn(prey, DesignationDefOf.Hunt);
                if (designation != null) { prey.Map.designationManager.RemoveDesignation(designation); withdrawn = true; }
            }
            if (hunter.CurJobDef == JobDefOf.Hunt && hunter.jobs != null)
            {
                hunter.jobs.EndCurrentJob(JobCondition.InterruptForced);
                withdrawn = true;
            }
            return withdrawn;
        }

        /// True when the hunter can reach the prey by a Danger.None touch route
        /// that never passes within predatorMarginCells (Chebyshev) of a wild
        /// predator. Go authors the margin on every caller's request or rule.
        internal static bool RouteSafe(Pawn hunter, Pawn prey, float predatorMarginCells)
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
                    Math.Max(Math.Abs(p.Position.x - cell.x), Math.Abs(p.Position.z - cell.z)) <= predatorMarginCells));
            }
        }
    }
}
