#nullable enable

using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// Eligibility for short observation windows, never proof of recovery.
    internal static class MedicalRestSafety
    {
        /// summaryHealthFloor is the policy's serious_summary_health_floor: a
        /// resting patient at or under it needs a fresh medical review.
        internal static bool Eligible(Pawn pawn, float summaryHealthFloor)
        {
            try {
                return pawn != null && pawn.Spawned && pawn.IsColonist && pawn.Downed && !pawn.Dead
                    && !pawn.Drafted && !pawn.InMentalState && pawn.InBed()
                    && pawn.health.summaryHealth.SummaryHealthPercent > summaryHealthFloor
                    && pawn.health.hediffSet.BleedRateTotal == 0f
                    && !pawn.health.HasHediffsNeedingTend(false)
                    && !pawn.health.hediffSet.HasHediff(HediffDefOf.Anesthetic)
                    && !pawn.health.hediffSet.hediffs.Any(h => h.IsCurrentlyLifeThreatening);
            } catch { return false; }
        }
    }
}
