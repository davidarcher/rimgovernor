using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Common;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameFacts.Receipts;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native;

internal static class NativeOrderedWork
{
    internal static OrderedWorkObservation? Latest(Pawn pawn)
    {
        var json = Current.Game.GetComponent<OrderedWorkState>()?.Read(pawn.GetUniqueLoadID());
        return json == null ? null : FactsJson.Deserialize<OrderedWorkObservation>(json);
    }

    private static void Write(OrderedWorkObservation observation) => OrderedWorkState.For(Current.Game)
        .Write(observation.Request.Pawn.Value, FactsJson.Serialize(observation));

    internal static OrderedWorkObservation? Observe(Pawn pawn)
    {
        var latest = Latest(pawn);
        if (latest == null) return null;
        var read = OrderedWorkProtocol.Observe(latest, pawn.CurJob?.loadID,
            pawn.jobs.jobQueue.Select(q => q.job.loadID).ToArray());
        if (read != latest) Write(read);
        return read;
    }

    internal static void Ending(Pawn pawn, JobCondition condition)
    {
        if (Current.Game == null || pawn.CurJob == null) return;
        var latest = Latest(pawn);
        if (latest == null) return;
        if (latest.Jobs.Any(j => j.JobId == pawn.CurJob.loadID && !OrderedWorkProtocol.Terminal(j.Status)))
            Write(OrderedWorkProtocol.Ended(latest, pawn.CurJob.loadID, condition == JobCondition.Succeeded));
    }

    private static Pawn? Pawn(OrderedWork request, ObservationContext context) => GameThread.LoadedMap(context)
        .mapPawns.FreeColonistsSpawned.ById(request.Pawn.Value);

    internal static Failure? Validate(OrderedWork request, ObservationContext context)
    {
        try { request.Validate(); }
        catch (DomainException e) { return GameThread.Fail(FailureCode.InvalidRequest, e.Message); }
        var pawn = Pawn(request, context);
        if (pawn == null) return GameThread.Fail(FailureCode.NotFound, "Ordered-work colonist is not spawned.");
        var operation = OrderedWorkProtocol.Operation(request, Observe(pawn));
        if (operation == OrderedWorkOperation.Refuse)
            return GameThread.Fail(FailureCode.InvalidRequest, "Ordered-work sequence is stale, altered or still active.");
        if (operation != OrderedWorkOperation.Install) return null;
        if (pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState || !pawn.IsColonistPlayerControlled
            || NativeBabyCare.Occupied(pawn) || pawn.jobs.jobQueue.Count != 0)
            return GameThread.Fail(FailureCode.InvalidRequest, "Ordered-work pawn is unavailable, caring or already has queued work.");
        if (!Build(request, pawn, out var built, out var failure)) return failure;
        foreach (var unused in built) JobMaker.ReturnToPool(unused.Job);
        return null;
    }

    private static bool Build(OrderedWork request, Pawn pawn, out List<WorkGiverJobResult> built, out Failure? failure)
    {
        built = new(); failure = null;
        foreach (var step in request.Jobs)
        {
            // Names are native-discovered boundary data, not hard-coded game definitions.
            var def = DefDatabase<JobDef>.GetNamedSilentFail(step.Job);
            var target = RefIndex.Thing(pawn.Map, step.Target);
            var job = def == null || target?.Spawned != true ? null : NativePrioritizedJob.Build(pawn, target, def, out _);
            if (job == null || pawn.CurJob?.JobIsSameAs(pawn, job.Job) == true)
            {
                foreach (var unused in built) JobMaker.ReturnToPool(unused.Job);
                if (job != null) JobMaker.ReturnToPool(job.Job);
                built.Clear();
                failure = GameThread.Fail(FailureCode.InvalidRequest, "Ordered work is unavailable or already running without this intervention's identity.");
                return false;
            }
            job.Job.playerForced = true;
            job.Job.workGiverDef = job.Giver;
            built.Add(job);
        }
        return true;
    }

    internal static EffectEvidence Apply(OrderedWork request, ObservationContext context)
    {
        var failure = Validate(request, context);
        if (failure != null) throw new ApplyRefusedException(failure);
        var pawn = Pawn(request, context) ?? throw new InvalidOperationException("Ordered-work pawn disappeared.");
        var latest = Observe(pawn);
        var operation = OrderedWorkProtocol.Operation(request, latest);
        var issued = false;
        if (operation == OrderedWorkOperation.Install)
        {
            if (!Build(request, pawn, out var built, out failure)) throw new ApplyRefusedException(failure!);
            // Reserve each currently legal job before changing execution. Queued jobs use vanilla's
            // reservations too; EnqueueLast alone does not make them.
            foreach (var next in built)
                if (!next.Job.TryMakePreToilReservations(pawn, errorOnFailed: false))
                {
                    foreach (var unused in built)
                    {
                        pawn.ClearReservationsForJob(unused.Job);
                        JobMaker.ReturnToPool(unused.Job);
                    }
                    throw new ApplyRefusedException(GameThread.Fail(FailureCode.InvalidRequest, "Ordered-work reservations changed."));
                }
            // Save identity before native callbacks can observe a start/end. No speculative future products.
            latest = new OrderedWorkObservation(request, EquatableArray.From(built.Select(j => new OrderedWorkJob(j.Job.loadID, OrderedWorkStatus.Queued))));
            Write(latest);
            var first = built[0];
            if (!pawn.jobs.TryTakeOrderedJob(first.Job, first.Giver.tagToGive))
            {
                latest = latest with { Jobs = EquatableArray.From(latest.Jobs.Select(j => j with { Status = OrderedWorkStatus.Gone })) };
                Write(latest);
                foreach (var unused in built)
                {
                    pawn.ClearReservationsForJob(unused.Job);
                    JobMaker.ReturnToPool(unused.Job);
                }
                throw new ApplyRefusedException(GameThread.Fail(FailureCode.InvalidRequest, "Native refused the ordered job."));
            }
            foreach (var next in built.Skip(1)) pawn.jobs.jobQueue.EnqueueLast(next.Job, next.Giver.tagToGive);
            issued = true;
        }
        else if (operation == OrderedWorkOperation.Cancel && latest != null)
        {
            var ids = new HashSet<int>(latest.Jobs.Where(j => !OrderedWorkProtocol.Terminal(j.Status)).Select(j => j.JobId));
            // Mark before cleanup callbacks; only identities still belonging to this intervention are cancelled.
            latest = latest with { Jobs = EquatableArray.From(latest.Jobs.Select(j => ids.Contains(j.JobId)
                ? j with { Status = OrderedWorkStatus.Cancelled } : j)) };
            Write(latest);
            pawn.jobs.jobQueue.RemoveAll(pawn, job => ids.Contains(job.loadID));
            if (pawn.CurJob is { } current && ids.Contains(current.loadID)) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }
        return new EffectEvidence { Effect = new JobEffect { PawnId = request.Pawn.Value, Issued = issued, Queue = Observe(pawn) } };
    }
}
