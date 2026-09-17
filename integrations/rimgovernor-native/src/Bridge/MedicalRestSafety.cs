#nullable enable

using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// Eligibility for short observation windows, never proof of recovery.
    internal static class MedicalRestSafety
    {
        internal static bool Eligible(Pawn pawn)
        {
            try {
                return pawn != null && pawn.Spawned && pawn.IsColonist && pawn.Downed && !pawn.Dead
                    && !pawn.Drafted && !pawn.InMentalState && pawn.InBed()
                    && pawn.health.summaryHealth.SummaryHealthPercent > 0.5f
                    && pawn.health.hediffSet.BleedRateTotal == 0f
                    && !pawn.health.HasHediffsNeedingTend(false)
                    && !pawn.health.hediffSet.HasHediff(HediffDefOf.Anesthetic)
                    && !pawn.health.hediffSet.hediffs.Any(h => h.IsCurrentlyLifeThreatening);
            } catch { return false; }
        }
    }
}
