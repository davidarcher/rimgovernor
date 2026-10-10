#nullable disable
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Runtime
{
    // Offers a shooting drill at a range stand to a colonist below the training target
    // (#2610). Whether the pawn takes it is the bot's work priority for the
    // RimGovernorTraining work type; this giver only says what the work is.
    public sealed class WorkGiver_TrainRange : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.OnCell;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => RangeTraining.Stands(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (!RangeTraining.Eligible(pawn, SkillDefOf.Shooting)) return true;
            foreach (var _ in RangeTraining.Stands(pawn.Map)) return false;
            return true;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) => Plan(pawn, t, out _);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!Plan(pawn, t, out var dummy)) return null;
            var job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(RangeTraining.ShootingJob), t, dummy);
            job.maxNumStaticAttacks = RangeTraining.SessionCycles;
            return job;
        }

        private static bool Plan(Pawn pawn, Thing stand, out Thing dummy)
        {
            dummy = null;
            if (!RangeTraining.Eligible(pawn, SkillDefOf.Shooting) || stand.Destroyed || !stand.Spawned || stand.IsForbidden(pawn)) return false;
            dummy = RangeTraining.DummyFor(stand);
            return dummy != null && !dummy.IsForbidden(pawn) && pawn.CanReserve(stand) && pawn.CanReserve(dummy);
        }
    }
}
