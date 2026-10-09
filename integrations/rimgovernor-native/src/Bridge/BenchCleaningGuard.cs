#nullable enable

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Opportunistic cleaning (#2515, epic #2510): when a pawn is about to start
    // a bill at a workbench, nearby home-area filth is cleaned first. Needs no
    // Go data. Caps apply to every trigger that shares CleanJob(): never override
    // a disabled or priority-0 Cleaning work type, at most MaxFilth filth per
    // trip, skip drafted, downed, mentally broken, bleeding, tending-needy,
    // player-forced or prioritised-work pawns, and act only under
    // Supervisor.IsActive. Forced bill orders (float menu, player orders) are
    // left alone. Each trip removes its filth, so the bill job returns once
    // none is left.
    internal static class BenchCleaningGuard
    {
        internal const int MaxFilth = 5;
        internal const int BenchRadius = 6;
        private static bool patched;
        internal static void Install()
        {
            if (patched) return;
            new Harmony("rimgovernor.bench-cleaning").Patch(
                AccessTools.Method(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.JobOnThing)),
                postfix: new HarmonyMethod(typeof(BenchCleaningGuard), nameof(BeforeBill)));
            patched = true;
        }

        private static void BeforeBill(Pawn pawn, Thing thing, bool forced, ref Job? __result)
        {
            if (forced || __result == null || __result.def != JobDefOf.DoBill || !Supervisor.IsActive
                || FloatMenuMakerMap.makingFor == pawn || !thing.Spawned) return;
            var clean = CleanJob(pawn, thing.Map, thing.def.hasInteractionCell ? thing.InteractionCell : thing.Position);
            if (clean != null) __result = clean;
        }

        // A Clean job over at most MaxFilth filth within BenchRadius of `near`,
        // or null when the pawn may not or need not clean.
        internal static Job? CleanJob(Pawn pawn, Map map, IntVec3 near)
        {
            if (!Eligible(pawn)) return null;
            var giver = NativeCleanOperations.Giver();
            if (giver == null) return null;
            var found = new List<Filth>();
            foreach (var cell in GenRadial.RadialCellsAround(near, BenchRadius, true))
            {
                if (!cell.InBounds(map)) continue;
                foreach (var t in cell.GetThingList(map))
                    if (found.Count < MaxFilth && t is Filth f && giver.HasJobOnThing(pawn, f, false)
                        && pawn.CanReach(f, PathEndMode.Touch, Danger.None))
                        found.Add(f);
                if (found.Count >= MaxFilth) break;
            }
            if (found.Count == 0) return null;
            var job = JobMaker.MakeJob(JobDefOf.Clean);
            foreach (var f in found) job.AddQueuedTarget(TargetIndex.A, f);
            return job;
        }

        internal static bool Eligible(Pawn pawn)
        {
            var cleaning = WorkTypeDefOf.Cleaning;
            if (pawn.Map == null || pawn.workSettings == null || pawn.Drafted || pawn.Downed || pawn.InMentalState
                || !pawn.RaceProps.Humanlike) return false;
            if (pawn.WorkTypeIsDisabled(cleaning) || pawn.workSettings.GetPriority(cleaning) == 0) return false;
            if (pawn.health.hediffSet.BleedRateTotal > 0.001f || pawn.health.HasHediffsNeedingTend()) return false;
            if (pawn.mindState.priorityWork.IsPrioritized) return false;
            return pawn.CurJob == null || !pawn.CurJob.playerForced;
        }
    }
}
