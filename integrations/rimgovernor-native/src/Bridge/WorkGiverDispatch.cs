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
    /// and takes the first job one produces for the target. Shared by the legacy
    /// OrderTool (haul/work/repair/clean) and the protobuf haul Operation dispatch
    /// so both issue exactly the same native job a player's click would produce.
    ///
    /// See OrderTool's original remarks: `Pawn_WorkSettings.WorkGiversInOrderNormal`
    /// is never used (its cache path logs and writes to the pawn); `workGiversByPriority`
    /// walked directly is what the float menu itself walks. `FloatMenuMakerMap.makingFor`
    /// is set for the duration because `WorkGiver_DoBill.StartOrResumeBillJob` gates
    /// its `JobFailReason.Is(...)` calls on it.
    /// </summary>
    internal static class WorkGiverDispatch
    {
        internal static WorkGiverJobResult? TryJob(Pawn? pawn, Thing? thing, Func<WorkGiverDef, bool> accept, out string? failReason)
        {
            failReason = null;
            List<WorkTypeDef> types;
            try { types = DefDatabase<WorkTypeDef>.AllDefsListForReading.ToList(); }
            catch { types = new List<WorkTypeDef>(); }

            Pawn? previous = null;
            var swapped = false;
            try
            {
                try
                {
                    previous = FloatMenuMakerMap.makingFor;
                    FloatMenuMakerMap.makingFor = pawn;
                    swapped = true;
                }
                catch { swapped = false; }

                try { JobFailReason.Clear(); } catch { }

                foreach (var type in types)
                {
                    if (type == null)
                        continue;
                    List<WorkGiverDef> givers;
                    try { givers = type.workGiversByPriority; }
                    catch { givers = null!; }
                    if (givers == null)
                        continue;

                    for (var i = 0; i < givers.Count; i++)
                    {
                        var giver = givers[i];
                        if (giver == null || !accept(giver))
                            continue;
                        if (!BridgeCommon.Try(() => giver.directOrderable, false))
                            continue;

                        var scanner = BridgeCommon.Try<WorkGiver_Scanner?>(() => giver.Worker as WorkGiver_Scanner, null);
                        if (scanner == null)
                            continue;
                        if (ScannerShouldSkip(pawn, scanner, thing))
                            continue;

                        var job = BridgeCommon.Try<Job?>(
                            () => scanner.HasJobOnThing(pawn, thing, true) ? scanner.JobOnThing(pawn, thing, true) : null,
                            null);
                        if (job == null)
                            continue;

                        try { job.workGiverDef = scanner.def; } catch { }
                        return new WorkGiverJobResult { Job = job, Scanner = scanner, Giver = giver, WorkType = type };
                    }
                }

                failReason = BridgeCommon.Try(() => JobFailReason.HaveReason, false)
                    ? BridgeCommon.SafeString(() => JobFailReason.Reason)
                    : null;
                return null;
            }
            finally
            {
                if (swapped)
                {
                    try { FloatMenuMakerMap.makingFor = previous; } catch { }
                }
                try { JobFailReason.Clear(); } catch { }
            }
        }

        /// <summary>`FloatMenuOptionProvider_WorkGivers.ScannerShouldSkip`,
        /// verbatim: a giver that does not even claim the thing is skipped
        /// before it is asked for a job.</summary>
        private static bool ScannerShouldSkip(Pawn? pawn, WorkGiver_Scanner scanner, Thing? t)
        {
            return !BridgeCommon.Try(() =>
            {
                var accepts = scanner.PotentialWorkThingRequest.Accepts(t);
                if (!accepts)
                {
                    var global = scanner.PotentialWorkThingsGlobal(pawn);
                    accepts = global != null && global.Contains(t);
                }
                return accepts && !scanner.ShouldSkip(pawn, true);
            }, false);
        }
    }
}
