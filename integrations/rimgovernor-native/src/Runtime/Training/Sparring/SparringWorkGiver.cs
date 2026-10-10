#nullable disable
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Runtime
{
    // Offers a place in a sparring bout at the ring (#2708). Forming the bout is
    // SparringBouts.PlanFor; the only thing this giver scans is the marker held
    // for the pawn. Whether the pawn takes it is the bot's work priority for the
    // RimGovernorTraining work type.
    public sealed class WorkGiver_Spar : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.OnCell;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !SparringBouts.Eligible(pawn) || SparringBouts.MarkerDef == null;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            var marker = SparringBouts.For(pawn.Map).PlanFor(pawn);
            if (marker != null) yield return marker;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return t.Spawned && !t.IsForbidden(pawn) && SparringBouts.For(pawn.Map).PlanFor(pawn) == t && pawn.CanReserve(t);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return HasJobOnThing(pawn, t, forced) ? JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(SparringBouts.JobName), t) : null;
        }
    }
}
