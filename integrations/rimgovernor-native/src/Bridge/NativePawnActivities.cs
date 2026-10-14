#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    /// <summary>Execution of already-journaled exact activities. No economic selection.</summary>
    internal static class NativePawnActivities
    {
        private sealed class Entry
        {
            internal Pawn Pawn = null!;
            internal PawnActivityOrder Order = null!;
            internal NativeControlIdentity Identity = null!;
            internal ulong Generation;
            internal int Step;
            internal string State = "installed", Reason = "";
            internal Job? Running;
            internal int RunningId;
            internal long Tick;
            internal Thing?[] Dependencies = [];
        }
        private sealed record Offer(Entry Entry, int Step, int JobId);
        private static readonly Dictionary<Pawn, Entry> Entries = new();
        private static readonly ConditionalWeakTable<Job, Offer> Offers = new();
        private sealed class ExactRecreation : JoyGiver_WatchBuilding
        {
            internal Job? At(Pawn pawn, Thing target) => CanInteractWith(pawn, target, false) ? TryGivePlayJob(pawn, target) : null;
        }
        private static bool installed;
        private static JobDef holdDef = null!;
        private static JobDef deliveryDef = null!;
        private static JobDef cookingDef = null!;
        private static bool replacing;
        internal static bool IsHold(Job job) => job.def == holdDef;
        internal static bool Owned(Pawn pawn) => Entries.TryGetValue(pawn, out var e) && !e.Order.Release;
        internal static Operations.PawnActivityExecution? Read(Pawn pawn) => Entries.TryGetValue(pawn, out var e) && !e.Order.Release ? Row(e) : null;
        private static Operations.PawnActivityExecution Row(Entry e) => new(e.Order.Assignment, e.Order.Method, e.Order.Revision,
            e.Step, e.State, e.Reason, e.Running == null ? null : e.RunningId, e.Tick);

        internal static void Install()
        {
            if (installed) return;
            holdDef = new JobDef { defName = "RimGovernorWait", driverClass = typeof(JobDriver_GovernorWait), reportString = "Waiting for the colony plan.", casualInterruptible = false };
            DefDatabase<JobDef>.Add(holdDef);
            deliveryDef = new JobDef { defName = "RimGovernorDeliver", driverClass = typeof(JobDriver_GovernorDeliver), reportString = "Delivering to the colony plan's destination.", casualInterruptible = false };
            DefDatabase<JobDef>.Add(deliveryDef);
            cookingDef = new JobDef { defName = "RimGovernorCook", driverClass = typeof(JobDriver_GovernorCook), reportString = "Cooking the colony plan's selected recipe.", casualInterruptible = false };
            DefDatabase<JobDef>.Add(cookingDef);
            var h = new Harmony("rimgovernor.activity-execution");
            void Patch(Type t, string name, string? before = null, string? after = null) => h.Patch(AccessTools.Method(t, name),
                before == null ? null : new HarmonyMethod(typeof(NativePawnActivities), before),
                after == null ? null : new HarmonyMethod(typeof(NativePawnActivities), after));
            Patch(typeof(Pawn_JobTracker), "DetermineNextJob", nameof(Select));
            Patch(typeof(Pawn_JobTracker), "DetermineNextConstantThinkTreeJob", nameof(Constant));
            Patch(typeof(Pawn_JobTracker), "CheckForJobOverride", nameof(Override));
            Patch(typeof(Pawn_JobTracker), "TryOpportunisticJob", nameof(Opportunistic));
            Patch(typeof(Pawn_JobTracker), "StartJob", nameof(Starting), nameof(Started));
            Patch(typeof(Pawn_JobTracker), "EndCurrentJob", nameof(Ending), nameof(Ended));
            Patch(typeof(Pawn_JobTracker), "TryTakeOrderedJob", nameof(Ordered));
            Patch(typeof(JobDriver), "DriverTick", after: nameof(DriverTick));
            Patch(typeof(JobDriver), "GetFinalizerJob", nameof(Finalizer));
            Patch(typeof(Pawn), "DeSpawn", nameof(Despawn));
            h.Patch(AccessTools.PropertySetter(typeof(Current), "Game"), prefix: new HarmonyMethod(typeof(NativePawnActivities), nameof(WorldChanging)));
            NativeControlAuthority.GenerationChanged += AuthorityChanged;
            installed = true;
        }

        internal static Common.Failure? Validate(PawnActivities? batch, Common.ObservationContext context)
        {
            try { batch?.Validate(); } catch (Exception ex) { return Refuse(ex.Message); }
            if (batch == null || !installed || context.NativeGeneration != batch.AuthorityGeneration) return Refuse("Missing/stale activity authority.");
            var status = NativeControlAuthority.ForGame(Current.Game).Status();
            if (!status.Active || status.Identity == null || status.Generation != batch.AuthorityGeneration
                || context.Identity?.ColonyId != status.Identity.ColonyId || context.Identity.LoadToken != status.Identity.LoadToken || context.Identity.MapId != status.Identity.MapId) return Refuse("Activity authority is inactive.");
            foreach (var order in batch.Orders)
            {
                var pawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == order.Pawn);
                if (pawn == null || (!order.Release && (!pawn.IsColonistPlayerControlled || pawn.Downed || pawn.InMentalState || pawn.Drafted))) return Refuse("Activity pawn is unavailable.");
                Entries.TryGetValue(pawn, out var old);
                if (old?.Order.Revision == order.Revision && Equivalent(old.Order, order)) continue;
                if ((old == null || old.Order.Release ? 0 : old.Order.Revision) != order.ExpectedRevision) return Refuse("Stale activity revision.");
                if (old != null && !old.Order.Release && (old.Order.Assignment != order.Assignment || old.Order.Method != order.Method)) return Refuse("Activity ownership changed.");
                if ((old == null || old.Order.Release) && (pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0)) return Refuse("Player/queued work must be explicitly resolved before control.");
                foreach (var step in order.Steps)
                {
                    if (step.Deadline <= Find.TickManager.TicksGame) return Refuse("Activity deadline already passed.");
                    if (step.WaitForFilth.Length != 0 && !(Thing(step.WaitForFilth) is Filth)) return Refuse("Unknown filth dependency.");
                    if (step.Kind == PawnActivityKind.Hold) continue;
                    var target = Thing(step.Target);
                    if (target == null || target.Destroyed || !target.Spawned || target.Map != pawn.Map) return Refuse("Activity target unavailable.");
                    if (step.Kind == PawnActivityKind.Work)
                    {
                        var giver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver);
                        if (giver?.Worker is not WorkGiver_Scanner || !Supported(giver, step.Job)
                            || pawn.WorkTagIsDisabled(giver.workTags) || (giver.workType != null && pawn.WorkTypeIsDisabled(giver.workType))) return Refuse("Unsupported/incapable activity work.");
                    }
                    if (step.Kind == PawnActivityKind.Eat && (!target.IngestibleNow || !pawn.RaceProps.CanEverEat(target) || step.Count > target.stackCount)) return Refuse("Selected food/count unavailable.");
                    if (step.Kind == PawnActivityKind.Recreation && (target.def.defName != "HorseshoesPin" || pawn.needs.joy == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))) return Refuse("Selected recreation unavailable.");
                    if (step.Kind == PawnActivityKind.Sleep && !RestUtility.CanUseBedNow(target, pawn, true)) return Refuse("Selected bed unavailable.");
                    if (step.Kind == PawnActivityKind.Deliver && !DeliveryAllowed(pawn, target, step)) return Refuse("Selected delivery unavailable.");
                    if (step.Kind == PawnActivityKind.Cook && !NativeActivityCooking.Resolve(pawn, target, step, out _, out _, out var cookingReason)) return Refuse(cookingReason);
                }
            }
            return null;
        }
        private static bool Supported(WorkGiverDef giver, string job) =>
            (job == "Clean" && giver.Worker is WorkGiver_CleanFilth) || (job == "Repair" && giver.Worker is WorkGiver_Repair)
            || (job == "Harvest" && giver.Worker is WorkGiver_GrowerHarvest)
            || (job == "BeatFire" && giver.giverClass.FullName == "RimWorld.WorkGiver_FightFires");
        private static bool Equivalent(PawnActivityOrder a, PawnActivityOrder b) => a.Pawn == b.Pawn && a.Assignment == b.Assignment
            && a.Method == b.Method && a.ExpectedRevision == b.ExpectedRevision && a.Revision == b.Revision && a.Release == b.Release && a.Steps.SequenceEqual(b.Steps);
        private static Common.Failure Refuse(string text) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, text);
        private static Thing? Thing(string id) => Find.CurrentMap.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id);

        private static bool DeliveryAllowed(Pawn pawn, Thing target, PawnActivityStep step)
        {
            if (step.Destination is not { } destination || target.def.category != ThingCategory.Item || !target.def.EverHaulable
                || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) || pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || pawn.carryTracker.CarriedThing != null
                || target.stackCount < step.Count || pawn.carryTracker.MaxStackSpaceEver(target.def) < step.Count) return false;
            var cell = new IntVec3(destination.X, 0, destination.Z);
            return JobDriver_GovernorDeliver.DestinationAvailable(pawn, cell)
                && pawn.CanReserveAndReach(cell, PathEndMode.OnCell, Danger.Some);
        }

        internal static Receipts.EffectEvidence Apply(PawnActivities batch, Common.ObservationContext context)
        {
            var invalid = Validate(batch, context);
            if (invalid != null) throw new InvalidOperationException(invalid.Detail);
            var identity = NativeControlAuthority.ForGame(Current.Game).Status().Identity!;
            var changed = new List<Entry>();
            var oldJobs = new List<(Pawn Pawn, Job? Job)>();
            // Validate all first; install every table row before callbacks can select any new work.
            foreach (var order in batch.Orders)
            {
                var pawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.First(p => p.GetUniqueLoadID() == order.Pawn);
                Entries.TryGetValue(pawn, out var old);
                if (old?.Order.Revision == order.Revision && Equivalent(old.Order, order)) continue;
                oldJobs.Add((pawn, order.Release ? old?.Running : old?.Running ?? pawn.CurJob));
                var e = new Entry { Pawn = pawn, Order = order with { Steps = order.Steps.ToArray() }, Identity = identity, Generation = batch.AuthorityGeneration,
                    Tick = Find.TickManager.TicksGame, Dependencies = order.Steps.Select(s => s.WaitForFilth.Length == 0 ? null : Thing(s.WaitForFilth)).ToArray() };
                Entries[pawn] = e;
                changed.Add(e);
            }
            replacing = true;
            try
            {
                foreach (var item in oldJobs)
                    if (item.Job != null && ReferenceEquals(item.Pawn.CurJob, item.Job)) item.Pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            }
            finally { replacing = false; }
            foreach (var e in changed)
            {
                // Retain only the last release receipt in the same row so an uncertain Hands retry is
                // idempotent. This row owns no job; a fresh assignment may acquire from revision zero.
                if (e.Order.Release) { e.State = "released"; Emit(e); }
                else Emit(e);
            }
            return new Receipts.EffectEvidence { Effect = new Receipts.PawnActivitiesEffect { Assignments = batch.Orders.Select(o =>
                Entries.Values.FirstOrDefault(e => e.Order.Pawn == o.Pawn) is { } e ? Row(e) : new Operations.PawnActivityExecution(o.Assignment, o.Method, o.Revision, 0, "released", "", null, Find.TickManager.TicksGame)).ToArray() } };
        }
        private static bool TryOwned(Pawn pawn, out Entry e)
        {
            if (!Entries.TryGetValue(pawn, out e!) || e.Order.Release) return false;
            var s = NativeControlAuthority.ForGame(Current.Game).Status();
            if (!s.Active || s.Generation != e.Generation || !ReferenceEquals(e.Identity.Game, Current.Game) || pawn.Map != e.Identity.Map)
            { Release(e); return false; }
            return true;
        }
        private static bool Select(Pawn ___pawn, ref ThinkResult __result, ref ThinkTreeDef thinkTree)
        {
            if (!TryOwned(___pawn, out var e)) return true;
            thinkTree = ___pawn.thinker.MainThinkTree;
            __result = ThinkResult.NoJob;
            if (e.Running != null && ReferenceEquals(e.Running, ___pawn.CurJob)) return false;
            try
            {
                if (___pawn.Downed || ___pawn.InMentalState || ___pawn.Drafted) { Fault(e, "involuntary_or_combat_state"); Release(e); return true; }
                var job = OfferJob(e);
                Offers.Remove(job); Offers.Add(job, new Offer(e, e.Step, job.loadID));
                if (e.State == "installed") { e.State = "offered"; e.Tick = Find.TickManager.TicksGame; Emit(e); }
                __result = new ThinkResult(job, ___pawn.thinker.MainThinkNodeRoot, JobTag.Misc);
            }
            catch (Exception ex) { Fault(e, ex.Message); }
            return false;
        }
        private static Job OfferJob(Entry e)
        {
            if (e.Step >= e.Order.Steps.Length || e.State == "failed")
            { Find.TickManager.Pause(); return JobMaker.MakeJob(holdDef); }
            var step = e.Order.Steps[e.Step];
            if (Find.TickManager.TicksGame >= step.Deadline) { Fault(e, "deadline"); return JobMaker.MakeJob(holdDef); }
            if (!Ready(e) || step.Kind == PawnActivityKind.Hold) return JobMaker.MakeJob(holdDef);
            var target = Thing(step.Target) ?? throw new InvalidOperationException("target_missing");
            var p = e.Pawn;
            if (target.IsForbidden(p) || !p.CanReserveAndReach(target, PathEndMode.Touch, Danger.Some)) throw new InvalidOperationException("target_unreachable_or_reserved");
            Job? job = null;
            if (step.Kind == PawnActivityKind.Work)
            {
                var giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed(step.WorkGiver).Worker;
                var eligible = giver is WorkGiver_GrowerHarvest ? giver.HasJobOnCell(p, target.Position, false) : giver.HasJobOnThing(p, target, false);
                if (p.WorkTagIsDisabled(giver.def.workTags) || (giver.def.workType != null && p.WorkTypeIsDisabled(giver.def.workType)) || giver.MissingRequiredCapacity(p) != null || !eligible) throw new InvalidOperationException("work_refused");
                if (step.Job is "Clean" or "Harvest") { job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(step.Job)); job.AddQueuedTarget(TargetIndex.A, target); }
                else job = giver.JobOnThing(p, target, false);
                if (job != null && job.def.defName != step.Job) { JobMaker.ReturnToPool(job); job = null; }
                if (job != null) job.workGiverDef = giver.def;
            }
            else if (step.Kind == PawnActivityKind.Eat)
            {
                if (!target.IngestibleNow || !p.RaceProps.CanEverEat(target) || target.stackCount < step.Count) throw new InvalidOperationException("food_unavailable");
                job = JobMaker.MakeJob(JobDefOf.Ingest, target); job.count = step.Count;
            }
            else if (step.Kind == PawnActivityKind.Sleep)
            {
                if (!RestUtility.CanUseBedNow(target, p, true)) throw new InvalidOperationException("bed_unavailable");
                job = JobMaker.MakeJob(JobDefOf.LayDown, target);
            }
            else if (step.Kind == PawnActivityKind.Deliver)
            {
                if (!DeliveryAllowed(p, target, step)) throw new InvalidOperationException("delivery_unavailable");
                var cell = step.Destination!.Value;
                job = JobMaker.MakeJob(deliveryDef, target, new IntVec3(cell.X, 0, cell.Z));
                job.count = step.Count;
                job.haulOpportunisticDuplicates = false;
            }
            else if (step.Kind == PawnActivityKind.Cook)
            {
                if (!NativeActivityCooking.Resolve(p, target, step, out var bill, out var ingredients, out var cookingReason)) throw new InvalidOperationException(cookingReason);
                job = JobMaker.MakeJob(cookingDef, target);
                job.bill = bill;
                job.targetQueueB = ingredients.Select(i => new LocalTargetInfo(i.Thing)).ToList();
                job.countQueue = ingredients.Select(i => i.Count).ToList();
                job.haulMode = HaulMode.ToCellNonStorage;
                job.workGiverDef = DefDatabase<WorkGiverDef>.GetNamed(step.WorkGiver);
            }
            if (step.Kind == PawnActivityKind.Recreation)
            {
                if (target.def.defName != "HorseshoesPin" || p.needs.joy == null || !p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) throw new InvalidOperationException("recreation_unavailable");
                job = new ExactRecreation { def = DefDatabase<JoyGiverDef>.GetNamed("Play_Horseshoes") }.At(p, target);
            }
            if (job == null) throw new InvalidOperationException("job_unavailable");
            job.playerForced = false;
            return job;
        }
        private static bool Ready(Entry e)
        {
            var dep = e.Dependencies[e.Step];
            if (dep == null) return true;
            // Exact known filth destroyed by cleaning is observed completion, not job success.
            if (dep.Destroyed) return true;
            if (!dep.Spawned || dep.Map != e.Pawn.Map) { Fault(e, "dependency_unavailable"); return false; }
            return false;
        }
        private static bool Constant(Pawn ___pawn, ref ThinkResult __result)
        { if (!TryOwned(___pawn, out _)) return true; __result = ThinkResult.NoJob; return false; }
        private static bool Override(Pawn ___pawn) => !TryOwned(___pawn, out _);
        private static bool Opportunistic(Pawn ___pawn, ref Job? __result)
        { if (!Owned(___pawn)) return true; __result = null; return false; }
        private static bool Finalizer(JobDriver __instance, ref Job? __result)
        { if (!Owned(__instance.pawn)) return true; __result = null; return false; }
        private static bool Starting(Pawn ___pawn, Job newJob, ref bool preToilReservationsCanFail)
        {
            if (!TryOwned(___pawn, out var e)) return true;
            if (___pawn.Downed || ___pawn.InMentalState || ___pawn.Drafted) { Fault(e, "involuntary_or_combat_state"); Release(e); return true; }
            if (!Offers.TryGetValue(newJob, out var offer) || offer.Entry != e || offer.Step != e.Step || offer.JobId != newJob.loadID)
            { Fault(e, "unauthorized_job_substitution"); return false; }
            e.Running = newJob; e.RunningId = newJob.loadID;
            preToilReservationsCanFail = true;
            return true;
        }
        private static void Started(Pawn ___pawn)
        {
            if (!Entries.TryGetValue(___pawn, out var e) || e.Running == null || !ReferenceEquals(___pawn.CurJob, e.Running)) return;
            if (e.State is "failed" or "completed") return;
            e.State = e.Running.def == holdDef ? "holding" : "started"; e.Tick = Find.TickManager.TicksGame; Emit(e);
        }
        private static void Ending(Pawn ___pawn, JobCondition condition, ref bool startNewJob, out bool __state)
        {
            __state = !replacing && Owned(___pawn) && startNewJob;
            if (__state) startNewJob = false;
            if (replacing || !Entries.TryGetValue(___pawn, out var e) || e.Running == null || !ReferenceEquals(e.Running, ___pawn.CurJob) || e.RunningId != ___pawn.CurJob.loadID) return;
            var hold = e.Running.def == holdDef;
            e.Running = null;
            if (e.State == "failed") return;
            if (condition == JobCondition.Succeeded && !hold) { e.Step++; e.State = e.Step == e.Order.Steps.Length ? "completed" : "installed"; e.Reason = "Succeeded"; }
            else if (condition != JobCondition.Succeeded) { e.State = "failed"; e.Reason = condition.ToString(); Find.TickManager.Pause(); }
            e.Tick = Find.TickManager.TicksGame; Emit(e);
        }
        private static void Ended(Pawn ___pawn, bool __state)
        {
            if (!__state || !Owned(___pawn) || ___pawn.CurJob != null) return;
            var result = ThinkResult.NoJob; ThinkTreeDef tree = ___pawn.thinker.MainThinkTree;
            Select(___pawn, ref result, ref tree);
            if (result.IsValid) ___pawn.jobs.StartJob(result.Job, JobCondition.None, result.SourceNode, thinkTree: tree, tag: result.Tag);
        }
        private static void Ordered(Pawn ___pawn)
        {
            if (Entries.TryGetValue(___pawn, out var e)) Release(e);
        }
        internal static void WaitTick(Pawn pawn)
        {
            if (!TryOwned(pawn, out var e)) { pawn.jobs.EndCurrentJob(JobCondition.InterruptForced); return; }
            if (e.State == "failed" || e.Step >= e.Order.Steps.Length) { Find.TickManager.Pause(); return; }
            var step = e.Order.Steps[e.Step];
            if (Find.TickManager.TicksGame >= step.Deadline) { Fault(e, "hold_deadline"); return; }
            if (!Ready(e)) return;
            if (step.Kind == PawnActivityKind.Hold && step.WaitForFilth.Length == 0) return;
            if (step.Kind == PawnActivityKind.Hold) e.Step++;
            e.State = e.Step == e.Order.Steps.Length ? "completed" : "installed";
            pawn.jobs.EndCurrentJob(JobCondition.Succeeded);
        }
        private static void DriverTick(JobDriver __instance)
        {
            var p = __instance.pawn;
            if (!Entries.TryGetValue(p, out var e) || e.Running == null || e.Running.def == holdDef || e.Step >= e.Order.Steps.Length) return;
            if (Find.TickManager.TicksGame < e.Order.Steps[e.Step].Deadline) return;
            p.jobs.EndCurrentJob(e.Order.Steps[e.Step].Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation ? JobCondition.Succeeded : JobCondition.Incompletable);
        }
        private static void Fault(Entry e, string reason)
        { if (e.State != "failed") { e.State = "failed"; e.Reason = reason; e.Tick = Find.TickManager.TicksGame; Emit(e); } Find.TickManager.Pause(); }
        private static void Release(Entry e)
        {
            Entries.Remove(e.Pawn);
            if (e.Running != null && ReferenceEquals(e.Pawn.CurJob, e.Running) && e.Pawn.CurJob.loadID == e.RunningId)
                e.Pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }
        private static void AuthorityChanged(NativeControlAuthority authority, NativeControlSnapshot snapshot, ulong old)
        { foreach (var e in Entries.Values.ToArray()) if (!snapshot.Active || snapshot.Generation != e.Generation) Release(e); }
        private static void WorldChanging() { foreach (var e in Entries.Values.ToArray()) Release(e); }
        private static void Despawn(Pawn __instance) { if (Entries.TryGetValue(__instance, out var e)) Release(e); }
        private static void Emit(Entry e)
        {
            PawnActivityTracker.Execution(Row(e), e.Pawn);
            if (e.State is "failed" or "completed") Supervisor.ActivityExecutionChanged(e.Order.Assignment + ":" + e.State + ":" + e.Reason);
        }
    }
    internal static partial class Supervisor
    {
        // Reuse the existing clock stop/journal/invalidation path, not a second event store.
        internal static void ActivityExecutionChanged(string detail)
        {
            if (_state?.Active == true) Stop(_state, "requested_pause", "pawn_activity:" + detail, true, null);
            else Find.TickManager.Pause();
        }
    }
    public sealed class JobDriver_GovernorWait : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;
        protected override IEnumerable<Toil> MakeNewToils()
        {
            var toil = ToilMaker.MakeToil("GovernorWait");
            toil.defaultCompleteMode = ToilCompleteMode.Never;
            toil.tickAction = () => NativePawnActivities.WaitTick(pawn);
            yield return toil;
        }
    }
    /// <summary>Native walking/reservation/carrying mechanics, with no storage search or nearby-drop fallback.</summary>
    public sealed class JobDriver_GovernorDeliver : JobDriver
    {
        internal static bool DestinationAvailable(Pawn pawn, IntVec3 cell) => cell.InBounds(pawn.Map)
            && cell.Standable(pawn.Map) && !cell.Fogged(pawn.Map) && !cell.IsForbidden(pawn)
            && !cell.GetThingList(pawn.Map).Any(t => t.def.category == ThingCategory.Item);

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, job.count, null, errorOnFailed)
            && pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnForbidden(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.B);
            this.FailOn(() => !DestinationAvailable(pawn, job.targetB.Cell));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                .FailOnSomeonePhysicallyInteracting(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A, failIfStackCountLessThanJobCount: true);
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            yield return Toils_General.DoAtomic(() =>
            {
                if (pawn.carryTracker.CarriedThing?.stackCount != job.count
                    || !DestinationAvailable(pawn, job.targetB.Cell)
                    || !pawn.carryTracker.TryDropCarriedThing(job.targetB.Cell, ThingPlaceMode.Direct, out var delivered)
                    || delivered == null || delivered.Position != job.targetB.Cell || delivered.stackCount != job.count)
                    EndJobWith(JobCondition.Incompletable);
            });
        }
    }
    internal sealed class PawnActivitiesActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativePawnActivities.Validate(action.Intent?.PawnActivities?.Batch, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativePawnActivities.Apply(action.Intent!.PawnActivities!.Batch!, context);
    }
}
