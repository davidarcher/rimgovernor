#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;
using Thing = Verse.Thing;

namespace RimGovernor.Native;

public sealed partial class PawnControlFixture
{
    private static Pawn? orderedWorker;
    private static Thing[] orderedTargets = [];
    private static readonly List<int> orderedStarts = new();
    private static bool orderedAdmitted, orderedPatched;

    [Tool("test/ordered_work_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage two legal independent deconstruction jobs and a healthy waiting worker. Does not dispatch the intervention.")]
    public async Task<object> PrepareOrderedWork(INativeContext ctx, CancellationToken ct, string pawn, string first, string second) =>
        await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = FixtureKit.PausedMap();
            orderedWorker = FixtureKit.Spawned<Pawn>(map, pawn);
            orderedTargets = [FixtureKit.Spawned<Thing>(map, first), FixtureKit.Spawned<Thing>(map, second)];
            orderedStarts.Clear(); orderedAdmitted = false;
            if (orderedWorker.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) throw new InvalidOperationException("Worker cannot construct.");
            orderedWorker.drafter.Drafted = false;
            orderedWorker.needs.food.CurLevelPercentage = 1;
            orderedWorker.needs.rest.CurLevelPercentage = 1;
            if (orderedWorker.needs.joy != null) orderedWorker.needs.joy.CurLevelPercentage = 1;
            foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !orderedWorker.WorkTypeIsDisabled(t)))
                orderedWorker.workSettings.SetPriority(type, type == WorkTypeDefOf.Construction ? 1 : 0);
            for (var h = 0; h < 24; h++) orderedWorker.timetable.SetAssignment(h, TimeAssignmentDefOf.Anything);
            foreach (var target in orderedTargets)
                map.designationManager.AddDesignation(new Designation(target, DesignationDefOf.Deconstruct));
            var waiting = JobMaker.MakeJob(JobDefOf.Wait);
            waiting.expiryInterval = 10000;
            waiting.playerForced = true;
            orderedWorker.jobs.StartJob(waiting, JobCondition.InterruptForced);
            if (!orderedPatched)
            {
                new Harmony("rimgovernor.fixture.ordered-work").Patch(AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)),
                    postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(OrderedWorkStarted)));
                orderedPatched = true;
            }
            return FixtureKit.Reply(OrderedWorkRead());
        }, ct).ConfigureAwait(false);

    private static void OrderedWorkStarted(Pawn ___pawn)
    {
        if (___pawn != orderedWorker || ___pawn.CurJob == null) return;
        // Read provenance without reconciling: the first start can precede appending the later jobs.
        var latest = NativeOrderedWork.Latest(___pawn);
        if (latest?.Jobs.Any(j => j.JobId == ___pawn.CurJob.loadID) == true && !orderedStarts.Contains(___pawn.CurJob.loadID))
            orderedStarts.Add(___pawn.CurJob.loadID);
    }

    private static Fx.OrderedWorkLifecycleRead OrderedWorkRead()
    {
        var pawn = orderedWorker ?? throw new InvalidOperationException("Prepare ordered work first.");
        var latest = NativeOrderedWork.Observe(pawn);
        return new()
        {
            Admitted = orderedAdmitted,
            IntendedJobs = latest?.Jobs.Select(j => j.JobId).ToArray() ?? [], StartedJobs = orderedStarts.ToArray(),
            Outcomes = latest?.Jobs.Select(j => j.Status.ToString()).ToArray() ?? [],
            TargetsRemoved = orderedTargets.All(t => t.Destroyed),
            ReturnedToNative = latest != null && latest.Jobs.All(j => j.Status == OrderedWorkStatus.Succeeded)
                && pawn.CurJob is { playerForced: false } && pawn.jobs.jobQueue.Count == 0,
        };
    }

    [Tool("test/ordered_work_read", Description = "Read actual finite queue identities, start order, end conditions, physical removals and native return. No dispatch.")]
    public async Task<object> ReadOrderedWork(INativeContext ctx, CancellationToken ct) =>
        await ctx.MainThread.InvokeAsync<object>(() => FixtureKit.Reply(OrderedWorkRead()), ct).ConfigureAwait(false);

    [Tool("test/ordered_work_admit", Description = "UNSAFE FOR MODEL EXECUTION. Admit one staged finite intervention through the live journal. Normal worker and Hands dispatch it; no direct native order from the fixture.")]
    public async Task<object> AdmitOrderedWork(INativeContext ctx, CancellationToken ct) =>
        await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (!orderedAdmitted)
            {
                var host = BrainBootstrap.Host ?? throw new InvalidOperationException("The in-process brain is required.");
                var review = host.Journal.LoadReview(ct);
                var world = review.Snapshot;
                var context = Context();
                if (!review.Enabled || !world.IsValid || world.Load.Value != context.Identity.LoadToken
                    || world.Native.Value != ((RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase)context.NativeGeneration).Value)
                    return FixtureKit.Reply(OrderedWorkRead());
                var pawn = orderedWorker ?? throw new InvalidOperationException("Prepare ordered work first.");
                var id = PlanIds.MintPlanId();
                var request = new OrderedWork(new(pawn.GetUniqueLoadID()), 1, "clear the staged obstructing walls",
                    EquatableArray.From(orderedTargets.Select(t => new OrderedWorkStep(JobDefOf.Deconstruct.defName, t.GetUniqueLoadID()))));
                var commit = host.Journal.CommitClaimedMethod(AdminFamily.WorkAssignments, world, new("ordered-work-lifecycle"),
                    "finite native queue mechanics precondition", new PlanSpec(id, new(1), [DomainAction.NewOrderedWorkAction(new(id.Value + "-queue"), request)]), ct);
                if (commit.Outcome != ClaimOutcome.Admitted)
                    throw new InvalidOperationException("Queue admission: " + commit.Outcome + " " + commit.Conflict?.Reason + " " + commit.Refusal?.Message);
                orderedAdmitted = true;
            }
            return FixtureKit.Reply(OrderedWorkRead());
        }, ct).ConfigureAwait(false);
}
