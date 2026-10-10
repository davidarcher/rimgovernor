#nullable disable
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimGovernor.Runtime
{
    // The "trained with" thought (#2710), shared by the range drill and the spar job.
    // A pawn whose session was completed (the drill's cycles, or a stop rule / exchange
    // cap / bout end; never a draft, downing, death or other interruption) gets it
    // when another colonist is on a training job within vanilla's chat range (6 cells,
    // line of sight), or, for sparring, when it fought in a bout. A colonist that
    // completed a session within CompanyTicks also counts as company, so two
    // shooters who finish a few hundred ticks apart both get it. Runtime only.
    public static class TrainingCompany
    {
        public const string ThoughtName = "RimGovernor_TrainedWith";
        public const int CompanyTicks = 2500;

        private static readonly string[] TrainingJobs = { "RimGovernor_TrainShooting", SparringBouts.JobName };
        private static readonly Dictionary<int, int> completedAt = new Dictionary<int, int>();

        // Called from a finish action after the pawn's gear is restored. sharedSession
        // is true when the session itself was shared (a bout); otherwise the pawn's
        // surroundings are asked.
        public static void Finished(Pawn pawn, bool completed, bool sharedSession)
        {
            if (!completed || pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted) return;
            var now = Find.TickManager.TicksGame;
            var company = sharedSession || NearTrainer(pawn, now);
            completedAt[pawn.thingIDNumber] = now;
            if (!company) return;
            var def = DefDatabase<ThoughtDef>.GetNamedSilentFail(ThoughtName);
            if (def != null) pawn.needs?.mood?.thoughts?.memories?.TryGainMemory(def);
        }

        private static bool NearTrainer(Pawn pawn, int now)
        {
            foreach (var other in pawn.Map.mapPawns.FreeColonistsSpawned)
            {
                if (other == pawn || !SocialInteractionUtility.IsGoodPositionForInteraction(pawn.Position, other.Position, pawn.Map)) continue;
                var job = other.CurJobDef?.defName;
                if (job != null && System.Array.IndexOf(TrainingJobs, job) >= 0) return true;
                if (completedAt.TryGetValue(other.thingIDNumber, out var tick) && now - tick >= 0 && now - tick <= CompanyTicks) return true;
            }
            return false;
        }
    }
}
