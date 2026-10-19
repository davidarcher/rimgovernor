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
            internal SelectedActivityTask? SelectedTask;
            internal NativeControlIdentity Identity = null!;
            internal ulong Generation;
            internal int Step;
            internal string State = "installed", Reason = "";
            internal Job? Running;
            internal int RunningId;
            internal long Tick;
            internal Thing?[] Dependencies = [];
            /// <summary>Cells sown under the step <see cref="SownStep"/> (a Sow step repeats one job per cell). In memory only: a load restarts the count (#3528).</summary>
            internal int Sown, SownStep = -1;
        }
        private sealed record Offer(Entry Entry, int Step, int JobId);
        private static readonly Dictionary<Pawn, Entry> Entries = new();
        private static readonly ConditionalWeakTable<Job, Offer> Offers = new();
        // The once-guard (taken before the work); installed is the readiness Validate reads (set after it).
        private static int installing;
        private const string HarmonyOwner = "rimgovernor.activity-execution";
        private static volatile bool installed;
        private static JobDef holdDef = null!;
        private static JobDef deliveryDef = null!;
        private static JobDef recipeDef = null!;
        private static JobDef tendDef = null!;
        private static bool replacing;
        // Ephemeral native call context, never assignment ownership or a retained permission.
        [ThreadStatic] private static Pawn? imposedEffectPawn;
        internal static bool IsHold(Job job) => job.def == holdDef;
        internal static bool Owned(Pawn pawn) => Entries.TryGetValue(pawn, out var e) && !e.Order.Release;
        /// <summary>Owned by an assignment other than standby (#3322): a standby colonist is free for new work.</summary>
        internal static bool Committed(Pawn pawn) => Entries.TryGetValue(pawn, out var e) && !e.Order.Release && !PawnActivities.IsStandby(e.Order);
        internal static PawnActivityStep? ActiveStep(Pawn pawn) => Entries.TryGetValue(pawn, out var e) && e.Step < e.Order.Steps.Length ? e.Order.Steps[e.Step] : null;
        internal static Operations.PawnActivityExecution? Read(Pawn pawn) => Entries.TryGetValue(pawn, out var e) && !e.Order.Release ? Row(e) : null;
        private static Operations.PawnActivityExecution Row(Entry e) => new(e.Order.Assignment, e.Order.Method, e.Order.Revision,
            e.Step, e.State, e.Reason, e.Running == null ? null : e.RunningId, e.Tick);

        internal static bool TrySelectPracticePair(Pawn pawn, Thing target, PawnActivityStep step)
        {
            if (!Entries.TryGetValue(pawn, out var own) || own.Order.Release || step.Practice is not { } practice) return false;
            var partner = Entries.Values.FirstOrDefault(e => e.Order.Pawn == practice.Partner && !e.Order.Release
                && e.Order.Method == own.Order.Method && e.Generation == own.Generation && e.Identity == own.Identity);
            if (partner?.SelectedTask?.Step is not { } other || other.Practice is not { } reciprocal
                || reciprocal.Partner != own.Order.Pawn || reciprocal.PartnerTarget != step.Target
                || other.Target != practice.PartnerTarget) return false;
            var partnerTarget = Thing(other.Target);
            return partnerTarget != null && NativePracticeActivities.Legal(partner.Pawn, partnerTarget, other) && RimGovernor.Runtime.SparringBouts.For(pawn.Map).SelectPair(pawn, target, partner.Pawn, partnerTarget);
        }
        internal static void Install()
        {
            if (System.Threading.Interlocked.CompareExchange(ref installing, 1, 0) != 0) return;
            holdDef = new JobDef { defName = "RimGovernorWait", driverClass = typeof(JobDriver_GovernorWait), reportString = "Waiting for the colony plan.", casualInterruptible = false };
            DefDatabase<JobDef>.Add(holdDef);
            deliveryDef = new JobDef { defName = "RimGovernorDeliver", driverClass = typeof(JobDriver_GovernorDeliver), reportString = "Delivering to the colony plan's destination.", casualInterruptible = false };
            DefDatabase<JobDef>.Add(deliveryDef);
            recipeDef = new JobDef { defName = "RimGovernorRecipe", driverClass = typeof(JobDriver_GovernorRecipe), reportString = "Making the colony plan's selected recipe.", casualInterruptible = false };
            DefDatabase<JobDef>.Add(recipeDef);
            tendDef = new JobDef { defName = "RimGovernorTend", driverClass = typeof(JobDriver_GovernorTend), reportString = "Performing the selected treatment.", casualInterruptible = false };
            DefDatabase<JobDef>.Add(tendDef);
            var h = new Harmony(HarmonyOwner);
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
            h.Patch(AccessTools.Method(typeof(HediffComp_Disorientation), nameof(HediffComp_Disorientation.CompPostTickInterval),
                    new[] { typeof(float).MakeByRefType(), typeof(int) }),
                prefix: new HarmonyMethod(typeof(NativePawnActivities), nameof(DisorientationStarting)),
                finalizer: new HarmonyMethod(typeof(NativePawnActivities), nameof(ImposedEffectEnded)));
            foreach (var type in new[] { typeof(CompAbilityEffect_PutToSleep), typeof(CompAbilityEffect_ForceJob) })
                h.Patch(AccessTools.Method(type, "Apply", new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) }),
                    prefix: new HarmonyMethod(typeof(NativePawnActivities), nameof(ImposedAbilityStarting)),
                    finalizer: new HarmonyMethod(typeof(NativePawnActivities), nameof(ImposedEffectEnded)));
            NativeControlAuthority.GenerationChanged += AuthorityChanged;
            installed = true;
        }

        /// <summary>How many times this installer's <c>StartJob</c> prefix is patched in: 1 once installed, more if it ran twice (#3289).</summary>
        internal static int StartJobPrefixes()
            => Harmony.GetPatchInfo(AccessTools.Method(typeof(Pawn_JobTracker), "StartJob"))?.Prefixes.Count(p => p.owner == HarmonyOwner) ?? 0;

        internal static Common.Failure? Validate(PawnActivities? batch, Common.ObservationContext context)
        {
            try { batch?.Validate(); } catch (Exception ex) { return Refuse(ex.Message); }
            if (batch == null || !installed || context.NativeGeneration != batch.AuthorityGeneration) return Refuse("Missing/stale activity authority.");
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return Refuse("Activity authority is unavailable.");
            var status = authority.Status();
            if (!status.Active || status.Identity == null || status.Generation != batch.AuthorityGeneration
                || context.Identity?.ColonyId != status.Identity.ColonyId || context.Identity.LoadToken != status.Identity.LoadToken || context.Identity.MapId != status.Identity.MapId) return Refuse("Activity authority is inactive.");
            foreach (var order in batch.Orders)
            {
                var pawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == order.Pawn);
                if (pawn == null || (!order.Release && (!pawn.IsColonistPlayerControlled || pawn.Downed || pawn.InMentalState || pawn.Drafted))) return Refuse("Activity pawn is unavailable.");
                Entries.TryGetValue(pawn, out var old);
                if (old?.Order.Revision == order.Revision && Equivalent(old.Order, order)) continue;
                // Another assignment's standby is a vacancy: any assignment acquires it from revision zero (#3322).
                var standby = old != null && PawnActivities.IsStandby(old.Order);
                var vacant = old == null || old.Order.Release || standby && old.Order.Assignment != order.Assignment;
                if ((vacant ? 0 : old!.Order.Revision) != order.ExpectedRevision) return Refuse("Stale activity revision.");
                if (!vacant && (old!.Order.Assignment != order.Assignment || old.Order.Method != order.Method)) return Refuse("Activity ownership changed.");
                if (vacant && (pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0)) return Refuse("Player/queued work must be explicitly resolved before control.");
                if (vacant && !standby && (order.OutputDelivery != null || batch.Tasks.Count != 0) && !NativeFoodActivity.Available(pawn)) return Refuse("Activity pawn is no longer idle.");
                // Standby never interrupts a running job it did not offer (an admitted direct job, a hold).
                if (vacant && !standby && PawnActivities.IsStandby(order) && pawn.CurJob != null && !NativeFoodActivity.Available(pawn)) return Refuse("Standby pawn is not idle.");
                foreach (var step in order.Steps)
                {
                    if (step.Deadline <= Find.TickManager.TicksGame) return Refuse("Activity deadline already passed.");
                    if (step.WaitForFilth.Length != 0 && !(Thing(step.WaitForFilth) is Filth)) return Refuse("Unknown filth dependency.");
                    if (step.Kind is PawnActivityKind.Hold or PawnActivityKind.Idle or PawnActivityKind.Standby) continue;
                    if (step.Kind == PawnActivityKind.Approach)
                    {
                        var destination = step.Destination!.Value;
                        if (!pawn.CanReach(new IntVec3(destination.X, 0, destination.Z), PathEndMode.OnCell, Danger.None)) return Refuse("Approach unavailable.");
                        continue;
                    }
                    if (step.Kind == PawnActivityKind.Work && step.Job == "BuildRoof")
                    {
                        var cell = new IntVec3(step.Destination!.Value.X, 0, step.Destination.Value.Z);
                        var roofGiver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver);
                        if (roofGiver?.Worker is not WorkGiver_BuildRoof || !cell.InBounds(pawn.Map)
                            || !pawn.Map.areaManager.BuildRoof[cell] || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
                            || !pawn.CanReach(cell, PathEndMode.Touch, Danger.None)) return Refuse("Selected roof work unavailable.");
                        continue;
                    }
                    if (step.Kind == PawnActivityKind.Work && step.Job == "Sow")
                    {
                        if (NativeSowingActivities.Giver?.defName != step.WorkGiver || NativeSowingActivities.NextJob(pawn, step, 0) is not { } probe)
                            return Refuse("Selected sowing unavailable.");
                        JobMaker.ReturnToPool(probe);
                        continue;
                    }
                    var target = Thing(step.Target) ?? (pawn.carryTracker.CarriedThing?.GetUniqueLoadID() == step.Target ? pawn.carryTracker.CarriedThing : null);
                    if (target == null || target.Destroyed || (!target.Spawned && pawn.carryTracker.CarriedThing != target)
                        || target.Spawned && target.Map != pawn.Map) return Refuse("Activity target unavailable.");
                    if (step.Practice != null && !NativePracticeActivities.Legal(pawn, target, step)) return Refuse("Selected practice unavailable.");
                    if (step.Kind == PawnActivityKind.Work)
                    {
                        var giver = DefDatabase<WorkGiverDef>.GetNamedSilentFail(step.WorkGiver);
                        if (giver?.Worker is not WorkGiver_Scanner || !Supported(giver, step.Job)
                            || pawn.WorkTagIsDisabled(giver.workTags) || (giver.workType != null && pawn.WorkTypeIsDisabled(giver.workType))) return Refuse("Unsupported/incapable activity work.");
                        if (step.Job == "Deconstruct" && DeconstructRefusal(pawn, target) is { } deconstructWhy) return Refuse("Selected deconstruction unavailable: " + deconstructWhy + ".");
                        if (step.Job == "Uninstall" && UninstallRefusal(pawn, target) is { } uninstallWhy) return Refuse("Selected uninstall unavailable: " + uninstallWhy + ".");
                    }
                    if (step.Kind == PawnActivityKind.Eat && (!target.IngestibleNow || !pawn.RaceProps.CanEverEat(target) || step.Count > target.stackCount)) return Refuse("Selected food/count unavailable.");
                    if (step.Kind == PawnActivityKind.Recreation && (NativeRecreation.GiverFor(target) is not { } joyGiver || !NativeRecreation.Usable(pawn, target, joyGiver))) return Refuse("Selected recreation unavailable.");
                    if (step.Kind == PawnActivityKind.Sleep && !RestUtility.CanUseBedNow(target, pawn, true)) return Refuse("Selected bed unavailable.");
                    if (step.Kind == PawnActivityKind.Deliver && DeliveryRefusal(pawn, target, step) is { } deliveryWhy) return Refuse("Selected delivery unavailable: " + deliveryWhy + ".");
                    if (step.Kind == PawnActivityKind.Supply && !SupplyAllowed(pawn, target, step)) return Refuse("Selected material delivery unavailable.");
                    if (step.Kind == PawnActivityKind.Recipe && !NativeActivityRecipe.Resolve(pawn, target, step, out _, out _, out var cookingReason)) return Refuse(cookingReason);
                }
            }
            foreach (var task in batch.Tasks.Where(t => t.Step.Practice != null))
            {
                var pawn = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == task.Pawn);
                var target = Thing(task.Step.Target);
                if (pawn == null || target == null || !NativePracticeActivities.Legal(pawn, target, task.Step)) return Refuse("Selected practice task unavailable.");
                if (task.Step.Practice!.Skill == "Melee")
                {
                    var partner = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == task.Step.Practice.Partner);
                    var partnerTarget = Thing(task.Step.Practice.PartnerTarget);
                    if (partner == null || partnerTarget == null || !RimGovernor.Runtime.SparringBouts.For(pawn.Map).CanSelectPair(pawn, target, partner, partnerTarget))
                        return Refuse("Selected sparring roster unavailable.");
                }
            }
            return null;
        }
        private static bool Supported(WorkGiverDef giver, string job) =>
            (job == "RimGovernor_TrainShooting" && giver.Worker is RimGovernor.Runtime.WorkGiver_TrainRange)
            || (job == "RimGovernor_Spar" && giver.Worker is RimGovernor.Runtime.WorkGiver_Spar)
            || (job == "DoBill" && giver.Worker is WorkGiver_DoBill) || (job == "TendPatient" && giver.Worker is WorkGiver_Tend) || (job == "Clean" && giver.Worker is WorkGiver_CleanFilth) || (job == "Repair" && giver.Worker is WorkGiver_Repair)
            || (job == "Harvest" && giver.Worker is WorkGiver_GrowerHarvest)
            || (job == "Research" && giver.Worker is WorkGiver_Researcher)
            || (job == "FinishFrame" && giver.Worker is WorkGiver_ConstructFinishFrames)
            || (job == "Deconstruct" && giver.Worker is WorkGiver_Deconstruct)
            || (job == "Uninstall" && giver.Worker is WorkGiver_Uninstall)
            || (job == "BeatFire" && giver.giverClass.FullName == "RimWorld.WorkGiver_FightFires");
        private static bool Equivalent(PawnActivityOrder a, PawnActivityOrder b) => PawnActivities.SameOrder(a, b);

        // A Deconstruct step works a standing building the order designates itself (StandRemoval), so an undesignated
        // ruin needs nothing first; the enclosure guard's roof-collapse wait is a refusal here, since the job would only hold (#3557).
        private static string? DeconstructRefusal(Pawn pawn, Thing target)
        {
            if (target is not Verse.Building building || !building.DeconstructibleBy(Faction.OfPlayer).Accepted) return "target is not deconstructible by the colony";
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) return "pawn is incapable of construction";
            if (NativeDesignationGuards.RoofWait(building) is { } roof) return roof;
            return building.Map.designationManager.DesignationOn(building, DesignationDefOf.Deconstruct) != null
                || new Designator_Deconstruct().CanDesignateThing(building).Accepted ? null : "native deconstruction designator refused";
        }

        // An Uninstall step packs a minifiable building of the colony's where it stands (#3612): the order stands the
        // designation itself, as the reconcile's Pack does; a Deconstruct designation or a reinstall blueprint on it owns it.
        private static string? UninstallRefusal(Pawn pawn, Thing target)
        {
            if (target is not Verse.Building building || !NativeConstructionActivity.Uninstallable(building)) return "target is not a minifiable building of the colony";
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) return "pawn is incapable of construction";
            if (NativeDesignationGuards.RoofWait(building) is { } roof) return roof;
            if (building.Map.designationManager.DesignationOn(building, DesignationDefOf.Deconstruct) != null) return "building is designated for deconstruction";
            return InstallBlueprintUtility.ExistingBlueprintFor(building) == null ? null : "building has a reinstall blueprint";
        }

        // Stands the removal designation the job needs (JobDriver_RemoveBuilding fails without it) under the enclosure
        // guard as an Ordered record: the roof-collapse wait still holds a deconstruction, revoking authority still
        // releases the designation, and the controlled pawn works it whether or not play is supervised. A Deconstruct is
        // placed through the game's designator; an Uninstall is the plain designation the reconcile's Pack stands.
        private static void StandRemoval(Verse.Building building, DesignationDef def)
        {
            var map = building.Map;
            if (map.designationManager.DesignationOn(building, def) == null)
            {
                if (def == DesignationDefOf.Deconstruct) new Designator_Deconstruct().DesignateThing(building);
                else map.designationManager.AddDesignation(new Designation(building, def));
            }
            var record = NativeDesignationGuards.Open(map, def, building.Position, building) ?? NativeDesignationGuards.Add(new GuardedDesignation
            {
                Id = "activity-" + Guid.NewGuid().ToString("N"), Guard = GuardNames.Enclosure, Designation = def.defName,
                ExpectedDef = building.def.defName, ThingId = building.GetUniqueLoadID(), MapId = map.uniqueID, X = building.Position.x, Z = building.Position.z,
            });
            record.Ordered = true;
        }
        private static Common.Failure Refuse(string text) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, text);
        private static Thing? Thing(string id) => RefIndex.Thing(Find.CurrentMap, id);

        private static bool DeliveryAllowed(Pawn pawn, Thing target, PawnActivityStep step) => DeliveryRefusal(pawn, target, step) == null;

        // Names the failed condition: a refusal the brain cannot tell apart is retried verbatim every review.
        private static string? DeliveryRefusal(Pawn pawn, Thing target, PawnActivityStep step)
        {
            if (step.Destination is not { } destination) return "no destination";
            if (target.def.category != ThingCategory.Item || !target.def.EverHaulable) return "target not haulable";
            if (pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) || pawn.WorkTagIsDisabled(WorkTags.ManualDumb)) return "pawn cannot haul";
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return "pawn cannot manipulate";
            if (pawn.carryTracker.CarriedThing != null && (pawn.carryTracker.CarriedThing != target || target.stackCount != step.Count)) return "pawn carries something else";
            if (target.stackCount < step.Count) return "stack smaller than count";
            if (pawn.carryTracker.MaxStackSpaceEver(target.def) < step.Count) return "count exceeds carry capacity";
            var cell = new IntVec3(destination.X, 0, destination.Z);
            if (NativeSupportActivities.Remaining(pawn.Map, step, target.def) < step.Count) return "bill needs fewer than count";
            if ((step.SupportDemand.Length != 0 ? NativeSupportActivities.DestinationRefusal(pawn, target, cell, step.Count) : JobDriver_GovernorDeliver.DestinationRefusal(pawn, cell)) is { } destinationWhy) return "destination (" + cell.x + "," + cell.z + ") not available for " + target.def.defName + " x" + step.Count + ": " + destinationWhy;
            return pawn.CanReserveAndReach(cell, PathEndMode.OnCell, Danger.Some) ? null : "destination cannot be reserved or reached";
        }

        internal static bool SupplyAllowed(Pawn pawn, Thing target, PawnActivityStep step)
        {
            var site = Thing(step.Container);
            return site is IConstructible construction && site.Faction == Faction.OfPlayer
                && !site.IsForbidden(pawn) && !target.IsForbidden(pawn) && target.stackCount >= step.Count
                && construction.ThingCountNeeded(target.def) >= step.Count
                && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) && !pawn.WorkTagIsDisabled(WorkTags.ManualDumb)
                && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                && (pawn.carryTracker.CarriedThing == null || pawn.carryTracker.CarriedThing == target && target.stackCount == step.Count)
                && pawn.carryTracker.MaxStackSpaceEver(target.def) >= step.Count
                && pawn.CanReserveAndReach(site, PathEndMode.Touch, Danger.None);
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
                    SelectedTask = batch.Tasks.FirstOrDefault(t => t.Id == order.Task), Tick = Find.TickManager.TicksGame, Dependencies = order.Steps.Select(s => s.WaitForFilth.Length == 0 ? null : Thing(s.WaitForFilth)).ToArray() };
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
                if (!e.Order.Release)
                    foreach (var step in e.Order.Steps)
                    {
                        if (step.Kind == PawnActivityKind.Work && step.Job is "Deconstruct" or "Uninstall" && Thing(step.Target) is Verse.Building building)
                            StandRemoval(building, step.Job == "Deconstruct" ? DesignationDefOf.Deconstruct : DesignationDefOf.Uninstall);
                        // A delivery of a stack under a Haul designation works the designation (#3614): a guarded record of it is Ordered.
                        else if (step.Kind == PawnActivityKind.Deliver && Thing(step.Target) is { Spawned: true } stack
                            && stack.Map.designationManager.DesignationOn(stack, DesignationDefOf.Haul) != null
                            && NativeDesignationGuards.Open(stack.Map, DesignationDefOf.Haul, stack.Position, stack) is { } record)
                            record.Ordered = true;
                    }
                // Retain only the last release receipt in the same row so an uncertain Hands retry is
                // idempotent. This row owns no job; a fresh assignment may acquire from revision zero.
                if (e.Order.Release) { e.State = "released"; Emit(e); }
                else Emit(e);
            }
            return new Receipts.EffectEvidence { Effect = new Receipts.EffectEvidenceEffect.PawnActivitiesCase() };
        }
        private static bool TryOwned(Pawn pawn, out Entry e)
        {
            if (!Entries.TryGetValue(pawn, out e!) || e.Order.Release) return false;
            if (pawn.Downed || pawn.InMentalState || pawn.Drafted || pawn.IsBurning())
            {
                // Standby held nothing: an involuntary or player state ends it quietly, as it would leave an unassigned pawn.
                if (PawnActivities.IsStandby(e.Order)) { Yield(e, "involuntary_or_emergency_state"); Release(e); return false; }
                Fault(e, "involuntary_or_emergency_state"); Release(e); return false;
            }
            var s = NativeControlAuthority.ForGame(Current.Game).Status();
            if (!s.Active || s.Generation != e.Generation || !ReferenceEquals(e.Identity.Game, Current.Game) || pawn.Map != e.Identity.Map)
            { Release(e); return false; }
            return true;
        }
        // Authority owns voluntary choice on its map. Exact Hands jobs may finish; native
        // physiology, combat, involuntary states and explicit player work remain effective.
        private static bool Controlled(Pawn pawn)
        {
            // Construction/load callbacks can run under a long event's worker thread, before
            // InitializeForCurrentGame establishes the main-thread authority. They must not
            // create an owner. Unexpected off-thread live play is a failure, not AI fallback.
            if (!UnityData.IsInMainThread)
            {
                if (Current.ProgramState == ProgramState.Playing && !LongEventHandler.AnyEventNowOrWaiting)
                    throw new InvalidOperationException("Voluntary job selection ran off the game thread outside loading.");
                return false;
            }
            if (Current.Game == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted
                || pawn.InMentalState || !pawn.IsColonistPlayerControlled || pawn.IsBurning()
                || pawn.CurJob?.playerForced == true || pawn.jobs.jobQueue.Count > 0) return false;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null) return false;
            var status = authority.Status();
            return status.Active && ReferenceEquals(status.Identity?.Map, pawn.Map);
        }
        private static bool Select(Pawn ___pawn, ref ThinkResult __result, ref ThinkTreeDef thinkTree)
        {
            if (!TryOwned(___pawn, out var e))
            {
                if (!Controlled(___pawn)) return true;
                // No authorized activity: no native job, no hold and no invalidation; the next rounds review assigns standby
                // (voluntary-control.md).
                __result = ThinkResult.NoJob;
                return false;
            }
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
            return JobMaker.MakeJob(holdDef);
            var step = e.Order.Steps[e.Step];
            if (Find.TickManager.TicksGame >= step.Deadline) { Fault(e, "deadline"); return JobMaker.MakeJob(holdDef); }
            var p = e.Pawn;
            if (step.Kind == PawnActivityKind.Standby) return StandbyJob(p);
            if (!Ready(e) || step.Kind is PawnActivityKind.Hold or PawnActivityKind.Idle) return JobMaker.MakeJob(holdDef);
            if (step.Kind == PawnActivityKind.Approach)
                return JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(step.Destination!.Value.X, 0, step.Destination.Value.Z));
            if (step.Kind == PawnActivityKind.Work && step.Job == "BuildRoof")
            {
                var cell = new IntVec3(step.Destination!.Value.X, 0, step.Destination.Value.Z);
                if (cell.GetRoof(p.Map) != null)
                {
                    e.Step++; e.State = e.Step == e.Order.Steps.Length ? "completed" : "installed";
                    e.Reason = "roof_observed"; e.Tick = Find.TickManager.TicksGame; Emit(e);
                    return OfferJob(e);
                }
                var roofGiver = DefDatabase<WorkGiverDef>.GetNamed(step.WorkGiver);
                if (roofGiver.Worker is not WorkGiver_BuildRoof roof || roof.MissingRequiredCapacity(p) != null
                    || p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) || !roof.HasJobOnCell(p, cell, false))
                    throw new InvalidOperationException("roof_work_refused");
                var roofJob = roof.JobOnCell(p, cell, false) ?? throw new InvalidOperationException("roof_work_refused");
                roofJob.workGiverDef = roofGiver; return roofJob;
            }
            if (step.Kind == PawnActivityKind.Work && step.Job == "Sow")
            {
                // One Sow job per cell: the step repeats until its count is sown or no cell is legal any more (a full zone is done, not a fault).
                var sowJob = NativeSowingActivities.NextJob(p, step, e.SownStep == e.Step ? e.Sown : 0);
                if (sowJob == null)
                {
                    e.Step++; e.State = e.Step == e.Order.Steps.Length ? "completed" : "installed";
                    e.Reason = "no_legal_cell"; e.Tick = Find.TickManager.TicksGame; Emit(e);
                    return OfferJob(e);
                }
                sowJob.workGiverDef = NativeSowingActivities.Giver; return sowJob;
            }
            var target = Thing(step.Target) ?? (p.carryTracker.CarriedThing?.GetUniqueLoadID() == step.Target ? p.carryTracker.CarriedThing : null)
                ?? throw new InvalidOperationException("target_missing");
            var (maxPawns, stackCount) = NativeNeedsForecast.ServiceReservation(step.Kind, target, step.Count);
            if (p.carryTracker.CarriedThing != target && (target.IsForbidden(p) || !p.CanReserveAndReach(target, PathEndMode.Touch, Danger.Some, maxPawns, stackCount))) throw new InvalidOperationException("target_unreachable_or_reserved");
            Job? job = null;
            if (step.Kind == PawnActivityKind.Work)
            {
                var giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed(step.WorkGiver).Worker;
                var eligible = step.Practice != null ? NativePracticeActivities.Legal(p, target, step) : step.Job == "DoBill" || (giver is WorkGiver_GrowerHarvest ? giver.HasJobOnCell(p, target.Position, false) : giver.HasJobOnThing(p, target, false));
                if (p.WorkTagIsDisabled(giver.def.workTags) || (giver.def.workType != null && p.WorkTypeIsDisabled(giver.def.workType)) || giver.MissingRequiredCapacity(p) != null || !eligible) throw new InvalidOperationException("work_refused");
                if (step.Practice != null) job = NativePracticeActivities.Job(p, target, step);
                else if (step.Job == "DoBill")
                {
                    if (target is not Pawn patient || !NativeSupportSurgery.Resolve(p, patient, step, out job)) throw new InvalidOperationException("selected_surgery_unavailable");
                }
                else if (step.Job == "TendPatient")
                {
                    if (target is not Pawn patient) throw new InvalidOperationException("care_patient_missing");
                    var medicine = step.Medicine.Length == 0 ? null : Thing(step.Medicine);
                    if (step.Medicine.Length != 0 && (medicine is not Medicine || !medicine.Spawned || medicine.IsForbidden(p)
                        || patient.playerSettings?.medCare.AllowsMedicine(medicine.def) != true || !p.CanReserveAndReach(medicine, PathEndMode.ClosestTouch, Danger.None)))
                        throw new InvalidOperationException("selected_medicine_unavailable");
                    job = JobMaker.MakeJob(tendDef, patient, medicine); job.count = 1;
                }
                else if (step.Job is "Clean" or "Harvest") { job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed(step.Job)); job.AddQueuedTarget(TargetIndex.A, target); }
                else job = giver.JobOnThing(p, target, false);
                if (job != null && job.def != tendDef && job.def.defName != step.Job) { JobMaker.ReturnToPool(job); job = null; }
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
            else if (step.Kind == PawnActivityKind.Recipe)
            {
                if (!NativeActivityRecipe.Resolve(p, target, step, out var bill, out var ingredients, out var cookingReason)) throw new InvalidOperationException(cookingReason);
                job = JobMaker.MakeJob(recipeDef, target);
                job.bill = bill;
                job.targetQueueB = ingredients.Select(i => new LocalTargetInfo(i.Thing)).ToList();
                job.countQueue = ingredients.Select(i => i.Count).ToList();
                job.haulMode = HaulMode.ToCellNonStorage;
                job.workGiverDef = DefDatabase<WorkGiverDef>.GetNamed(step.WorkGiver);
            }
            else if (step.Kind == PawnActivityKind.Supply)
            {
                if (!SupplyAllowed(p, target, step)) throw new InvalidOperationException("supply_unavailable");
                var site = Thing(step.Container)!;
                job = JobMaker.MakeJob(JobDefOf.HaulToContainer, target, site, site);
                job.count = step.Count; job.haulOpportunisticDuplicates = false;
            }
            if (step.Kind == PawnActivityKind.Recreation)
            {
                if (NativeRecreation.GiverFor(target) is not { } joyGiver || !NativeRecreation.Usable(p, target, joyGiver)) throw new InvalidOperationException("recreation_unavailable");
                job = NativeRecreation.PlayJob(p, target, joyGiver);
            }
            if (job == null) throw new InvalidOperationException("job_unavailable");
            // The admitted Method owns this service window; native joy and tolerance still end it.
            if (step.Kind == PawnActivityKind.Recreation) job.ignoreJoyTimeAssignment = true;
            job.playerForced = false;
            return job;
        }
        private static readonly JobGiver_WanderColony StandbyWander = new();
        /// <summary>Standby (#3322): the colony wander giver's own GotoWander/Wait_Wander, else a short Wait_Wander.
        /// No work giver runs; needs are served only by the assignments that replace it.</summary>
        private static Job StandbyJob(Pawn pawn)
        {
            var job = StandbyWander.TryIssueJobPackage(pawn, default).Job;
            if (job != null) return job;
            job = JobMaker.MakeJob(JobDefOf.Wait_Wander);
            job.expiryInterval = 180;
            return job;
        }
        private static bool Ready(Entry e)
        {
            if (Find.TickManager.TicksGame < e.Order.Steps[e.Step].NotBefore) return false;
            var dep = e.Dependencies[e.Step];
            if (dep == null) return true;
            // Exact known filth destroyed by cleaning is observed completion, not job success.
            if (dep.Destroyed) return true;
            if (!dep.Spawned || dep.Map != e.Pawn.Map) { Fault(e, "dependency_unavailable"); return false; }
            return false;
        }
        private static bool Constant(Pawn ___pawn, ref ThinkResult __result)
        { if (!TryOwned(___pawn, out _) && !Controlled(___pawn)) return true; __result = ThinkResult.NoJob; return false; }
        private static bool Override(Pawn ___pawn) => !TryOwned(___pawn, out _) && !Controlled(___pawn);
        private static bool Opportunistic(Pawn ___pawn, ref Job? __result)
        { if (!TryOwned(___pawn, out _) && !Controlled(___pawn)) return true; __result = null; return false; }
        private static bool Finalizer(JobDriver __instance, ref Job? __result)
        { if (!TryOwned(__instance.pawn, out _) && !Controlled(__instance.pawn)) return true; __result = null; return false; }
        private static void DisorientationStarting(HediffComp_Disorientation __instance, out Pawn? __state)
        { __state = imposedEffectPawn; imposedEffectPawn = __instance.Pawn; }
        private static void ImposedAbilityStarting(LocalTargetInfo target, out Pawn? __state)
        { __state = imposedEffectPawn; imposedEffectPawn = target.Pawn; }
        private static Exception? ImposedEffectEnded(Exception? __exception, Pawn? __state)
        { imposedEffectPawn = __state; return __exception; }
        private static bool Starting(Pawn ___pawn, Job newJob, ref bool preToilReservationsCanFail)
        {
            // Exhaustion starts LayDown directly from Need_Rest, before the pawn becomes downed.
            // Hediff symptoms likewise start the exact Vomit job directly. Neither is voluntary.
            // Player jobs likewise
            // take ownership before TryTakeOrderedJob's authority-revocation postfix runs.
            if (newJob.startInvoluntarySleep || newJob.def == JobDefOf.Vomit || newJob.playerForced || ReferenceEquals(imposedEffectPawn, ___pawn))
            {
                if (Entries.TryGetValue(___pawn, out var interrupted) && PawnActivities.IsStandby(interrupted.Order))
                { Yield(interrupted, newJob.playerForced ? "player_order" : "involuntary_job"); Entries.Remove(___pawn); }
                else if (Entries.TryGetValue(___pawn, out interrupted) && !interrupted.Order.Release)
                {
                    interrupted.State = "failed";
                    interrupted.Reason = newJob.startInvoluntarySleep ? "involuntary_sleep"
                        : newJob.def == JobDefOf.Vomit ? "involuntary_vomit" : newJob.playerForced ? "player_order" : "imposed_native_effect";
                    interrupted.Tick = Find.TickManager.TicksGame;
                    Emit(interrupted);
                    Release(interrupted);
                }
                return true;
            }
            if (!TryOwned(___pawn, out var e))
            {
                if (!Controlled(___pawn) || NativeControlAuthority.TryGetForGame(Current.Game, out var authority) && authority?.IsOwned == true) return true;
                return false;
            }
            if (___pawn.Downed || ___pawn.InMentalState || ___pawn.Drafted) { Fault(e, "involuntary_or_combat_state"); Release(e); return true; }
            var offered = Offers.TryGetValue(newJob, out var offer) && offer.Entry == e && offer.Step == e.Step && offer.JobId == newJob.loadID;
            // An admitted Hands-scope direct job (a hunt, an equip) takes a standby pawn as it took an unassigned one.
            if (!offered && PawnActivities.IsStandby(e.Order) && NativeControlAuthority.TryGetForGame(Current.Game, out var scope) && scope?.IsOwned == true)
            { Yield(e, "admitted_direct_job"); Entries.Remove(___pawn); return true; }
            if (!offered)
            { Fault(e, "unauthorized_job_substitution"); return false; }
            if (e.Step < e.Order.Steps.Length && e.Order.Steps[e.Step].Kind == PawnActivityKind.Recipe && newJob.bill == null)
            {
                if (!NativeActivityRecipe.Resolve(___pawn, newJob.targetA.Thing, e.Order.Steps[e.Step], out var bill, out _, out var reason, create: true))
                { Fault(e, reason); return false; }
                newJob.bill = bill;
            }
            e.Running = newJob; e.RunningId = newJob.loadID;
            preToilReservationsCanFail = true;
            return true;
        }
        private static void Started(Pawn ___pawn)
        {
            if (!Entries.TryGetValue(___pawn, out var e) || e.Running == null || !ReferenceEquals(___pawn.CurJob, e.Running)) return;
            if (e.State is "failed" or "completed") return;
            var state = e.Running.def == holdDef ? "holding" : "started";
            if (state == e.State && PawnActivities.IsStandby(e.Order)) return; // One row per standby, not per wander leg.
            e.State = state; e.Tick = Find.TickManager.TicksGame; Emit(e);
        }
        private static void Ending(Pawn ___pawn, JobCondition condition, ref bool startNewJob, out bool __state)
        {
            __state = !replacing && Owned(___pawn) && startNewJob;
            if (__state) startNewJob = false;
            if (replacing || !Entries.TryGetValue(___pawn, out var e) || e.Running == null || !ReferenceEquals(e.Running, ___pawn.CurJob) || e.RunningId != ___pawn.CurJob.loadID) return;
            var hold = e.Running.def == holdDef;
            NativeActivityRecipe.Cleanup(e.Running.bill);
            e.Running = null;
            if (e.State == "failed") return;
            // Standby never completes or fails on a job's end: Ended offers the next wander leg, and time keeps running.
            if (PawnActivities.IsStandby(e.Order)) return;
            if (condition == JobCondition.Succeeded && !hold)
            {
                // A Sow step stays current until its cell count is sown; every other step is done with its job.
                var sowing = e.Step < e.Order.Steps.Length && e.Order.Steps[e.Step] is { Kind: PawnActivityKind.Work, Job: "Sow" } sow ? sow : null;
                if (sowing != null)
                {
                    if (e.SownStep != e.Step) { e.SownStep = e.Step; e.Sown = 0; }
                    e.Sown++;
                }
                if (sowing == null || e.Sown >= sowing.Count) e.Step++;
                e.State = e.Step == e.Order.Steps.Length ? "completed" : "installed"; e.Reason = "Succeeded";
            }
            else if (condition != JobCondition.Succeeded) { e.State = "failed"; e.Reason = condition.ToString(); }
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
            // A terminal assignment retains ownership for continuation, not permission to advance time.
            if (e.State == "failed" || e.Step >= e.Order.Steps.Length) return;
            var step = e.Order.Steps[e.Step];
            if (step.Kind == PawnActivityKind.Idle)
            {
                if (Find.TickManager.TicksGame < step.Deadline) return;
                e.Step++; e.State = "completed"; e.Reason = "reconsider_due"; e.Tick = Find.TickManager.TicksGame;
                Emit(e); pawn.jobs.EndCurrentJob(JobCondition.Succeeded); return;
            }
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
            var step = e.Order.Steps[e.Step];
            if (step.Kind == PawnActivityKind.Deliver && step.SupportDemand.Length != 0
                && (__instance.job.targetA.Thing ?? p.carryTracker.CarriedThing) is { } source
                && NativeSupportActivities.Remaining(p.Map, step, source.def) < step.Count)
            { p.jobs.EndCurrentJob(JobCondition.Incompletable); return; }
            if (step.Practice is { } practice && !NativePracticeActivities.Assigned(p, practice))
            { p.jobs.EndCurrentJob(JobCondition.Succeeded); return; }
            // The forecast's sleep ends at the wake threshold; the native LayDown toil wakes there but never completes, so a
            // rested pawn would lie awake in bed, still owned, until the planned end (#3598).
            if (step.Kind == PawnActivityKind.Sleep && !__instance.asleep && p.needs.rest != null && RestUtility.ShouldWakeUp(p))
            { p.jobs.EndCurrentJob(JobCondition.Succeeded); return; }
            if (Find.TickManager.TicksGame < step.Deadline) return;
            p.jobs.EndCurrentJob(step.Practice != null || e.Order.Steps[e.Step].Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation ? JobCondition.Succeeded : JobCondition.Incompletable);
        }
        /// <summary>Records a standby's end without a terminal state: nothing waits on it, so time keeps running.</summary>
        private static void Yield(Entry e, string reason)
        { e.State = "released"; e.Reason = reason; e.Tick = Find.TickManager.TicksGame; PawnActivityTracker.Execution(Row(e), e.Pawn); }
        private static void Fault(Entry e, string reason)
        { if (e.State != "failed") { e.State = "failed"; e.Reason = reason; e.Tick = Find.TickManager.TicksGame; Emit(e); } }
        private static void Release(Entry e)
        {
            Entries.Remove(e.Pawn);
            if (e.Pawn.Map != null && RimGovernor.Runtime.SparringBouts.For(e.Pawn.Map).BoutOf(e.Pawn)?.Selected == true)
                RimGovernor.Runtime.SparringBouts.For(e.Pawn.Map).Leave(e.Pawn);
            if (e.Running != null && ReferenceEquals(e.Pawn.CurJob, e.Running) && e.Pawn.CurJob.loadID == e.RunningId)
                e.Pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }
        private static void AuthorityChanged(NativeControlAuthority authority, NativeControlSnapshot snapshot, ulong old)
        {
            foreach (var e in Entries.Values.ToArray()) if (!snapshot.Active || snapshot.Generation != e.Generation) Release(e);
            if (snapshot.Active && snapshot.Identity?.Map is { } map)
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToArray())
                    if (Controlled(pawn) && !Owned(pawn) && pawn.CurJob != null)
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }
        private static void WorldChanging() { foreach (var e in Entries.Values.ToArray()) Release(e); }
        private static void Despawn(Pawn __instance) { if (Entries.TryGetValue(__instance, out var e)) Release(e); }
        private static void Emit(Entry e) { PawnActivityTracker.Execution(Row(e), e.Pawn); }
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
        internal static bool DestinationAvailable(Pawn pawn, IntVec3 cell) => DestinationRefusal(pawn, cell) == null;
        internal static string? DestinationRefusal(Pawn pawn, IntVec3 cell)
        {
            if (!cell.InBounds(pawn.Map) || !cell.Standable(pawn.Map) || cell.Fogged(pawn.Map) || cell.IsForbidden(pawn)) return "cell not standable, fogged or forbidden";
            var item = cell.GetThingList(pawn.Map).FirstOrDefault(t => t.def.category == ThingCategory.Item);
            return item == null ? null : "cell holds " + item.def.defName + " x" + item.stackCount;
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            (pawn.carryTracker.CarriedThing == job.targetA.Thing || pawn.Reserve(job.targetA, job, 1, job.count, null, errorOnFailed))
            && pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnForbidden(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.B);
            bool DestinationValid() => NativePawnActivities.ActiveStep(pawn)?.SupportDemand.Length > 0
                ? NativeSupportActivities.DestinationAllowed(pawn, pawn.carryTracker.CarriedThing ?? job.targetA.Thing, job.targetB.Cell, job.count)
                : DestinationAvailable(pawn, job.targetB.Cell);
            this.FailOn(() => !DestinationValid());
            if (pawn.carryTracker.CarriedThing != job.targetA.Thing)
            {
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                    .FailOnSomeonePhysicallyInteracting(TargetIndex.A);
                yield return Toils_Haul.StartCarryThing(TargetIndex.A, failIfStackCountLessThanJobCount: true);
            }
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            yield return Toils_General.DoAtomic(() =>
            {
                var carried = pawn.carryTracker.CarriedThing;
                var before = job.targetB.Cell.GetThingList(pawn.Map).Where(t => t.def == carried?.def).Sum(t => t.stackCount);
                if (carried?.stackCount != job.count
                    || !DestinationValid()
                    || !pawn.carryTracker.TryDropCarriedThing(job.targetB.Cell, ThingPlaceMode.Direct, out var delivered)
                    || delivered == null || delivered.Position != job.targetB.Cell || delivered.stackCount != before + job.count)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                // A Haul designation rides the carried stack (only Toils_Haul.PlaceHauledThingInCell and destruction clear
                // it): the delivered stack has been hauled, as the vanilla haul clears it on placing (#3614).
                pawn.Map.designationManager.TryRemoveDesignationOn(carried, DesignationDefOf.Haul);
                pawn.Map.designationManager.TryRemoveDesignationOn(delivered, DesignationDefOf.Haul);
            });
        }
    }
    internal sealed class PawnActivitiesActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativePawnActivities.Validate(action.Intent?.PawnActivities?.Batch, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativePawnActivities.Apply(action.Intent!.PawnActivities!.Batch!, context);
    }
}
