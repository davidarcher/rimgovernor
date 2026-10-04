#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal sealed class WorkGiverJobResult
    {
        internal Job Job = null!;
        internal WorkGiver_Scanner Scanner = null!;
        internal WorkGiverDef Giver = null!;
        internal WorkTypeDef WorkType = null!;
    }

    /// <summary>
    /// Walks every WorkGiverDef the way `FloatMenuOptionProvider_WorkGivers` does
    /// and takes the first job one produces for the target, so the protobuf haul
    /// Operation dispatch issues exactly the native job a player's click would produce.
    ///
    /// `Pawn_WorkSettings.WorkGiversInOrderNormal`
    /// is never used (its cache path logs and writes to the pawn); `workGiversByPriority`
    /// walked directly is what the float menu itself walks. `FloatMenuMakerMap.makingFor`
    /// is set for the duration because `WorkGiver_DoBill.StartOrResumeBillJob` gates
    /// its `JobFailReason.Is(...)` calls on it.
    /// </summary>
    internal static class WorkGiverDispatch
    {
        internal static WorkGiverJobResult? TryJob(Pawn? pawn, Thing? thing, Func<WorkGiverDef, bool> accept, out string? failReason, JobDef? jobDef = null)
        {
            failReason = null;
            var types = DefDatabase<WorkTypeDef>.AllDefsListForReading.ToList();

            var previous = FloatMenuMakerMap.makingFor;
            FloatMenuMakerMap.makingFor = pawn;
            try
            {
                JobFailReason.Clear();

                foreach (var type in types)
                {
                    if (type == null)
                        continue;
                    var givers = type.workGiversByPriority;
                    if (givers == null)
                        continue;

                    for (var i = 0; i < givers.Count; i++)
                    {
                        var giver = givers[i];
                        if (giver == null || !accept(giver))
                            continue;
                        if (!giver.directOrderable)
                            continue;

                        var scanner = giver.Worker as WorkGiver_Scanner;
                        if (scanner == null)
                            continue;
                        if (ScannerShouldSkip(pawn, scanner, thing))
                            continue;

                        var job = scanner.HasJobOnThing(pawn, thing, true) ? scanner.JobOnThing(pawn, thing, true) : null;
                        if (job == null || (jobDef != null && job.def != jobDef))
                            continue;

                        job.workGiverDef = scanner.def;
                        return new WorkGiverJobResult { Job = job, Scanner = scanner, Giver = giver, WorkType = type };
                    }
                }

                failReason = JobFailReason.HaveReason
                    ? JobFailReason.Reason
                    : null;
                return null;
            }
            finally
            {
                FloatMenuMakerMap.makingFor = previous;
                JobFailReason.Clear();
            }
        }

        /// <summary>`FloatMenuOptionProvider_WorkGivers.ScannerShouldSkip`,
        /// verbatim: a giver that does not even claim the thing is skipped
        /// before it is asked for a job.</summary>
        private static bool ScannerShouldSkip(Pawn? pawn, WorkGiver_Scanner scanner, Thing? t)
        {
            var accepts = scanner.PotentialWorkThingRequest.Accepts(t);
            if (!accepts)
            {
                var global = scanner.PotentialWorkThingsGlobal(pawn);
                accepts = global != null && global.Contains(t);
            }
            return !(accepts && !scanner.ShouldSkip(pawn, true));
        }
    }
}
