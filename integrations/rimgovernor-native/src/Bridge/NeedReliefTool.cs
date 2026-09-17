#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Uses ordinary native need job givers. Never changes needs, thoughts,
    // timetables, restrictions, traits, ideology or mental states.
    public sealed class NeedReliefTools
    {
        private sealed class Recreation : JobGiver_GetJoy
        {
            protected override bool JoyGiverAllowed(JoyGiverDef def) => !(def.Worker is JoyGiver_Ingest);
        }

        [Tool("home/relieve_need", Title = "Offer ordinary need recovery",
            Description = "Offer one native food/rest/recreation job to an undrafted colonist. Preserves player-forced jobs, queues and schedules; refuses active mental breaks and medical rest. Preview checks admission only; the native job giver selects an eligible target on dispatch. An accepted job does not prove need recovery.")]
        public async Task<object> Relieve(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact observed pawn Thing ID.")] string pawn,
            [ToolParameter(Description = "food, rest or joy.")] string need,
            [ToolParameter(Description = "Exact observed current job load ID, or -1 when idle.")] int expectedJob,
            [ToolParameter(Description = "Exact observed current timetable assignment def name.")] string expectedSchedule,
            [ToolParameter(Description = "Admission preview only; does not generate a job.", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var p = Find.CurrentMap?.mapPawns.FreeColonistsSpawned.SingleOrDefault(x => x.GetUniqueLoadID() == pawn);
                if (p == null || p.Dead || p.Downed || p.Drafted || p.InMentalState || !p.IsColonistPlayerControlled)
                    return new { success = false, error = "Pawn unavailable, drafted or in an active mental break." };
                string? error = null;
                if (p.CurJob?.loadID != (expectedJob == -1 ? (int?)null : expectedJob)
                    || p.CurJob?.playerForced == true || p.jobs.jobQueue.Count != 0)
                    error = "Current job changed or player work is protected.";
                else if (p.timetable?.CurrentAssignment?.defName != expectedSchedule)
                    error = "Player timetable changed.";
                else if (HealthAIUtility.ShouldSeekMedicalRest(p))
                    error = "Medical rest takes precedence.";
                else if (!p.jobs.IsCurrentJobPlayerInterruptible() || p.carryTracker?.CarriedThing != null || p.IsBurning())
                    error = "Current job, carried cargo or fire prevents safe interruption.";
                if (error != null) return new { success = false, error };
                ThinkNode_JobGiver giver;
                float? level;
                if (need == "rest") { giver = new JobGiver_GetRest(); level = p.needs?.rest?.CurLevelPercentage; }
                else if (need == "food") { giver = new JobGiver_GetFood(); level = p.needs?.food?.CurLevelPercentage; }
                else if (need == "joy") { giver = new Recreation(); level = p.needs?.joy?.CurLevelPercentage; }
                else return new { success = false, error = "Unknown need." };
                if (level == null || level >= 0.5f)
                    return new { success = false, error = "Need is absent or no longer deficient." };
                var assignment = p.timetable?.CurrentAssignment;
                if (need == "joy" ? assignment != TimeAssignmentDefOf.Anything && assignment != TimeAssignmentDefOf.Joy
                                  : giver.GetPriority(p) <= 0)
                    return new { success = false, error = "Native need priority or player timetable prevents recovery now." };
                if (dryRun) return new { success = true, dryRun, canTry = true, level };
                giver.ResolveReferences();
                var job = giver.TryIssueJobPackage(p, default(JobIssueParams)).Job;
                if (job == null) return new { success = false, error = "No eligible native need job; inspect access, resources and recreation tolerance." };
                // Keep recovery bounded to consumption, resting and ordinary recreation.
                if (need == "food" && job.def != JobDefOf.Ingest)
                    return new { success = false, error = "Food recovery requires an available ingestible; production remains a separate goal." };
                if (job.targetA.IsValid && (!p.CanReach(job.targetA, PathEndMode.Touch, Danger.None)
                    || job.targetA.Cell.IsForbidden(p)))
                    return new { success = false, error = "Need target is not safely reachable under current restrictions." };
                if (!job.TryMakePreToilReservations(p, errorOnFailed: false))
                {
                    p.ClearReservationsForJob(job);
                    return new { success = false, error = "Native job reservations refused recovery." };
                }
                // Start an ordinary AI job, so later timetable/player changes can
                // interrupt it. TryTakeOrderedJob would mark it player-forced.
                p.jobs.StartJob(job, JobCondition.InterruptForced, giver, preToilReservationsCanFail: true);
                bool accepted = p.CurJob == job;
                return new { success = accepted && p.CurJob == job, accepted, dryRun,
                    pawn, need, before = level, job = job.def.defName, jobId = job.loadID,
                    error = accepted ? null : "Native job reservations refused recovery." };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
