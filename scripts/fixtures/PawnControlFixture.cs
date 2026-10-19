#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
namespace HomeBridge.BridgeTools
{
    // The control cases (#3398): a staged producer, helper, crop, meal and bed; the dependency filth, which the stage
    // primitives cannot express; Hands executions installed straight into the native execution table; and one native
    // lifecycle probe per case.
    public sealed partial class PawnControlFixture
    {
        private static Pawn? worker, helper;
        private static Thing? crop, filth, food, bed;
        private static long deadline;
        private static PawnActivityOrder? workerOrder, helperOrder;
        private static readonly MethodInfo Choose = AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob")
            ?? throw new MissingMethodException("Pawn_JobTracker.DetermineNextJob");

        private static Common.ObservationContext Context()
        {
            var s=NativeControlAuthority.ForGame(Current.Game).Status();
            return new Common.ObservationContext { NativeGeneration=s.Generation, Tick=Find.TickManager.TicksGame,
                Identity=new Common.Identity { ColonyId=s.Identity!.ColonyId, LoadToken=s.Identity.LoadToken, MapId=s.Identity.MapId } };
        }

        // Every colonist waits on a long job until an execution takes it; under authority an unowned job cannot start.
        private static void Still(Map map)
        {
            foreach (var p in map.mapPawns.FreeColonistsSpawned.ToArray()) p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait,120000),JobCondition.InterruptForced);
        }

        private static void Apply(params PawnActivityOrder[] orders) => NativePawnActivities.Apply(new PawnActivities(Context().NativeGeneration!.Value, orders), Context());

        // The worker's next revision of its current order, with these steps.
        private static PawnActivityOrder Revised(PawnActivityOrder order, params PawnActivityStep[] steps) =>
            order with { ExpectedRevision=order.Revision, Revision=order.Revision+1, Release=false, Steps=steps };

        private static PawnActivityOrder Install(PawnActivityOrder order) { Apply(order); return workerOrder = order; }

        private static ThinkResult Offer() => (ThinkResult)(Choose.Invoke(worker!.jobs,new object?[]{null,false}) ?? throw new InvalidOperationException("Missing think result"));

        private static PawnActivityStep Hold(long until) => new PawnActivityStep(PawnActivityKind.Hold,"","","",0,until);

        [Tool("test/pawn_control_prepare", Description="UNSAFE FOR MODEL EXECUTION. Bind the staged producer, helper, crop, meal and bed; spawn aged dependency filth on the crop cell; stand every colonist still.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken ct,
            [ToolParameter(Description="The producer's load id.")] string worker = "",
            [ToolParameter(Description="The helper's load id.")] string helper = "",
            [ToolParameter(Description="The ripe crop's load id.")] string crop = "",
            [ToolParameter(Description="The meal's load id.")] string meal = "",
            [ToolParameter(Description="The bed's load id.")] string bed = "") => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map=FixtureKit.PausedMap();
            PawnControlFixture.worker=FixtureKit.Spawned<Pawn>(map,worker);
            PawnControlFixture.helper=FixtureKit.Spawned<Pawn>(map,helper);
            PawnControlFixture.crop=FixtureKit.Spawned<Plant>(map,crop);
            food=FixtureKit.Spawned<Thing>(map,meal);
            PawnControlFixture.bed=FixtureKit.Spawned<Building_Bed>(map,bed);
            Still(map);
            filth=GenSpawn.Spawn(ThingDefOf.Filth_Dirt,PawnControlFixture.crop!.Position,map);
            // Native clean eligibility uses the filth age, not merely its existence.
            (AccessTools.Field(typeof(Filth),"growTick") ?? throw new MissingFieldException("Filth.growTick")).SetValue(filth, -10000);
            deadline=Find.TickManager.TicksGame+10000;
            return FixtureKit.Reply(new Fx.FixtureDone { Success=true });
        },ct).ConfigureAwait(false);

        [Tool("test/pawn_control_install",Description="UNSAFE FOR MODEL EXECUTION. Validate and install the producer's dependency hold, harvest, meal and sleep, the helper's cleaning, and standby for every other colonist.")]
        public async Task<object> Install(IRimBridgeContext ctx,CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(()=>
        {
            var c=Context();
            var clean=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.Worker is WorkGiver_CleanFilth).defName;
            var harvest=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.Worker is WorkGiver_GrowerHarvest).defName;
            workerOrder=new PawnActivityOrder(worker!.GetUniqueLoadID(),"producer","food-method",0,1,false,new[]{
                new PawnActivityStep(PawnActivityKind.Hold,"","","",0,deadline,filth!.GetUniqueLoadID()),
                new PawnActivityStep(PawnActivityKind.Work,crop!.GetUniqueLoadID(),harvest,"Harvest",0,deadline),
                new PawnActivityStep(PawnActivityKind.Eat,food!.GetUniqueLoadID(),"","",1,deadline),
                new PawnActivityStep(PawnActivityKind.Sleep,bed!.GetUniqueLoadID(),"","",0,deadline)});
            helperOrder=new PawnActivityOrder(helper!.GetUniqueLoadID(),"helper","food-method",0,1,false,new[]{
                new PawnActivityStep(PawnActivityKind.Work,filth!.GetUniqueLoadID(),clean,"Clean",0,deadline),
                Hold(deadline)});
            var batch=new PawnActivities(c.NativeGeneration!.Value,new[]{workerOrder,helperOrder}.Concat(worker.Map.mapPawns.FreeColonistsSpawned
                .Where(p => p != worker && p != helper).Select(p => new PawnActivityOrder(p.GetUniqueLoadID(),
                    "fixture-idle-" + p.GetUniqueLoadID(), "food-method", 0, 1, false,
                    [new(PawnActivityKind.Standby, "", "", "", 0, long.MaxValue)]))).ToArray());
            var invalid=NativePawnActivities.Validate(batch,c); if(invalid!=null)throw new InvalidOperationException(invalid.Detail);
            NativePawnActivities.Apply(batch,c);
            NativePawnActivities.Apply(batch,c); // Idempotent reinstall must not restart work.
            var before=NativePawnActivities.Read(worker);
            for(var i=0;i<8;i++)worker.jobs.CheckForJobOverride();
            if(NativePawnActivities.Read(worker)!=before)throw new InvalidOperationException("Override probes consumed an assignment.");
            var stale=batch with { Orders=new[]{workerOrder with { Revision=2,ExpectedRevision=0 }} };
            if(NativePawnActivities.Validate(stale,c)==null)throw new InvalidOperationException("Stale revision accepted.");
            return FixtureKit.Reply(new Fx.FixtureDone { Success=true });
        },ct).ConfigureAwait(false);

        [Tool("test/pawn_control_read",Description="UNSAFE FOR MODEL EXECUTION. Read the producer's execution, job and needs, and whether the filth is cleaned and the crop harvested.")]
        public async Task<object> Read(IRimBridgeContext ctx,CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(()=>FixtureKit.Reply(new Fx.ControlRead {
            Worker=Execution(NativePawnActivities.Read(worker!)),
            Job=worker!.CurJob?.def.defName, Food=worker.needs.food.CurLevelPercentage, Rest=worker.needs.rest.CurLevelPercentage,
            FilthGone=filth!.Destroyed, CropHarvested=((Plant)crop!).Growth<1 || crop.Destroyed,
            Forced=worker.CurJob?.playerForced==true,
        }),ct).ConfigureAwait(false);

        internal static Fx.ActivityExecution? Execution(RimGovernor.Contracts.Wire.Operations.PawnActivityExecution? e) =>
            e == null ? null : new Fx.ActivityExecution { State=e.State, Reason=e.Reason, Revision=e.Revision };

        [Tool("test/pawn_control_loaded",Description="UNSAFE FOR MODEL EXECUTION. Assert a real reload discarded all old execution ownership.")]
        public async Task<object> Loaded(IRimBridgeContext ctx,CancellationToken ct)=>await ctx.MainThread.InvokeAsync<object>(()=>
        {
            if(Find.CurrentMap.mapPawns.AllPawnsSpawned.Any(NativePawnActivities.Owned) || (worker!=null && NativePawnActivities.Owned(worker)))
                throw new InvalidOperationException("Load restored activity ownership.");
            return FixtureKit.Reply(new Fx.FixtureDone { Success=true });
        },ct).ConfigureAwait(false);

        [Tool("test/pawn_control_probe",Description="UNSAFE FOR MODEL EXECUTION. One bounded native lifecycle probe over the installed executions: atomic, deadline, refusal, preemption, release, overrides or player.")]
        public async Task<object> Probe(IRimBridgeContext ctx,CancellationToken ct,
            [ToolParameter(Description="The probe: atomic, deadline, refusal, preemption, release, overrides or player.")] string probe = "")=>await ctx.MainThread.InvokeAsync<object>(()=>
        {
            if(workerOrder==null||helperOrder==null)throw new InvalidOperationException("Install the executions first.");
            switch(probe)
            {
                case "atomic": Atomic(); break;
                case "deadline": Deadline(); break;
                case "refusal": Refusal(); break;
                case "preemption": Preemption(); break;
                case "release": Release(); break;
                case "overrides": Overrides(); break;
                case "player": Player(); break;
                default: throw new ArgumentException($"Unknown probe \"{probe}\".");
            }
            return FixtureKit.Reply(new Fx.FixtureDone { Success=true });
        },ct).ConfigureAwait(false);

        // A batch with one invalid order changes nothing, the valid order included.
        private static void Atomic()
        {
            var c=Context();
            var original=NativePawnActivities.Read(worker!)!;
            var invalid=new PawnActivities(c.NativeGeneration!.Value,new[]{
                Revised(workerOrder!,Hold(deadline)),
                Revised(helperOrder!,new PawnActivityStep(PawnActivityKind.Eat,"missing","","",1,deadline))});
            if(NativePawnActivities.Validate(invalid,c)==null || NativePawnActivities.Read(worker!)!=original)throw new InvalidOperationException("Invalid batch was not atomic.");
        }

        // A hold's deadline ends its authorization without resetting, and the failed task-backed hold holds the clock.
        private static void Deadline()
        {
            Install(Revised(workerOrder!,Hold(Find.TickManager.TicksGame+2)) with {Task="terminal-fixture"});
            var offer=Offer();
            if(!offer.IsValid)throw new InvalidOperationException("Hold offer missing.");
            worker!.jobs.StartJob(offer.Job,JobCondition.InterruptForced,offer.SourceNode);
            for(var i=0;i<3;i++)Find.TickManager.DoSingleTick();
            if(NativePawnActivities.Read(worker)?.Reason!="hold_deadline")throw new InvalidOperationException("Hold deadline did not terminate authorization.");
            Find.TickManager.CurTimeSpeed=TimeSpeed.Normal;
            NativePawnActivities.WaitTick(worker);
            if(!Find.TickManager.Paused)throw new InvalidOperationException("Failed task-backed hold allowed time without continuation.");
        }

        // Work the native game refuses (repairing an undamaged bed) fails the execution visibly.
        private static void Refusal()
        {
            var repair=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.Worker is WorkGiver_Repair).defName;
            Install(Revised(workerOrder!,new PawnActivityStep(PawnActivityKind.Work,bed!.GetUniqueLoadID(),repair,"Repair",0,deadline)));
            _=Offer();
            if(NativePawnActivities.Read(worker!)?.State!="failed")throw new InvalidOperationException("Native work refusal was hidden.");
        }

        // Selected recreation starts as a normal job, and a selected emergency preempts it.
        private static void Preemption()
        {
            var map=worker!.Map;
            var pinCell=GenRadial.RadialCellsAround(worker.Position,10,false).First(x=>x.InBounds(map)&&x.Standable(map)&&x.GetEdifice(map)==null&&x.DistanceTo(worker.Position)>4);
            var pin=GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("HorseshoesPin"),pinCell,map);
            var recreation=Install(Revised(workerOrder!,new PawnActivityStep(PawnActivityKind.Recreation,pin.GetUniqueLoadID(),"","",0,deadline)));
            var joyOffer=Offer();
            if(!joyOffer.IsValid)throw new InvalidOperationException("Recreation offer missing.");
            worker.jobs.StartJob(joyOffer.Job,JobCondition.None,joyOffer.SourceNode);
            if(worker.CurJob?.def.defName!="Play_Horseshoes" || worker.CurJob.playerForced)throw new InvalidOperationException("Selected recreation did not start normally.");
            var fireCell=GenRadial.RadialCellsAround(worker.Position,8,false).First(x=>x.InBounds(map)&&x.Standable(map)&&x.GetEdifice(map)==null&&x.DistanceTo(worker.Position)>3);
            var fire=GenSpawn.Spawn(ThingDefOf.Fire,fireCell,map);
            map.areaManager.Home[fireCell]=true;
            var fireGiver=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.giverClass.FullName=="RimWorld.WorkGiver_FightFires").defName;
            Install(Revised(recreation,new PawnActivityStep(PawnActivityKind.Work,fire.GetUniqueLoadID(),fireGiver,"BeatFire",0,deadline)));
            var fireOffer=Offer();
            if(!fireOffer.IsValid)throw new InvalidOperationException("Emergency offer missing.");
            worker.jobs.StartJob(fireOffer.Job,JobCondition.None,fireOffer.SourceNode);
            if(worker.CurJob?.def!=JobDefOf.BeatFire || worker.CurJob.playerForced)throw new InvalidOperationException("Emergency did not preempt recreation.");
        }

        // A repeated release drops ownership; the released pawn neither falls through to voluntary selection nor starts
        // an unowned direct job; only an admitted Hands scope starts one; and an old release cannot erase a newer assignment.
        private static void Release()
        {
            // One revision past the fresh assignment's, so the old release below differs from the colliding one.
            var held=Install(Revised(workerOrder!,Hold(deadline)));
            var release=held with {ExpectedRevision=held.Revision,Revision=held.Revision+1,Release=true,Steps=Array.Empty<PawnActivityStep>()};
            Apply(release);
            Apply(release);
            if(NativePawnActivities.Owned(worker!)||NativePawnActivities.Read(worker!)!=null)throw new InvalidOperationException("Repeated release retained ownership.");
            var withoutOwner=Offer();
            if (withoutOwner.IsValid || !Find.TickManager.Paused)
                throw new InvalidOperationException("Unassigned controlled pawn fell through to native voluntary selection.");
            var direct = JobMaker.MakeJob(JobDefOf.Wait, 40);
            worker!.jobs.StartJob(direct, JobCondition.InterruptForced);
            if (ReferenceEquals(worker.CurJob, direct)) throw new InvalidOperationException("Unowned direct StartJob bypassed voluntary control.");
            var admitted = JobMaker.MakeJob(JobDefOf.Wait, 40);
            using (NativeControlAuthority.ForGame(Current.Game).Owned()) worker.jobs.StartJob(admitted, JobCondition.InterruptForced);
            if (!ReferenceEquals(worker.CurJob, admitted)) throw new InvalidOperationException("Admitted Hands scope could not start an exact job.");
            Install(Fresh());
            var collidingRelease=release with {ExpectedRevision=1,Revision=2};
            if(NativePawnActivities.Validate(new PawnActivities(Context().NativeGeneration!.Value,new[]{collidingRelease}),Context())==null)throw new InvalidOperationException("Release matched revision but ignored changed assignment.");
            if(NativePawnActivities.Validate(new PawnActivities(Context().NativeGeneration!.Value,new[]{release}),Context())==null)throw new InvalidOperationException("Old release could erase a newer assignment.");
        }

        // A fresh assignment for the worker once nothing owns it.
        private static PawnActivityOrder Fresh() => workerOrder! with {Assignment="fresh-assignment",ExpectedRevision=0,Revision=1,Release=false,Steps=new[]{Hold(deadline)}};

        // Native involuntary sleep, physiological vomiting and an imposed sleep each release voluntary ownership, and the
        // imposed effect's permission ends with its source.
        private static void Overrides()
        {
            Apply(workerOrder! with {ExpectedRevision=workerOrder.Revision,Revision=workerOrder.Revision+1,Release=true,Steps=Array.Empty<PawnActivityStep>()});
            Install(Fresh());
            var exhaustion = JobMaker.MakeJob(JobDefOf.LayDown, worker!.Position);
            exhaustion.startInvoluntarySleep = true;
            worker.jobs.StartJob(exhaustion, JobCondition.InterruptForced, preToilReservationsCanFail: true);
            if (NativePawnActivities.Owned(worker) || !ReferenceEquals(worker.CurJob, exhaustion))
                throw new InvalidOperationException("Native involuntary sleep did not release voluntary ownership.");
            Install(Fresh());
            var vomiting=JobMaker.MakeJob(JobDefOf.Vomit);
            worker.jobs.StartJob(vomiting,JobCondition.InterruptForced,resumeCurJobAfterwards:true);
            if(NativePawnActivities.Owned(worker)||!ReferenceEquals(worker.CurJob,vomiting))
                throw new InvalidOperationException("Native physiological vomiting did not release voluntary ownership.");
            // Native resumption can queue the interrupted hold; remove that fixture artifact before the next probe.
            worker.jobs.ClearQueuedJobs();
            Install(Fresh());
            var imposedSleep=new CompAbilityEffect_PutToSleep {parent=new RimWorld.Ability(worker),props=new CompProperties_AbilityEffect()};
            imposedSleep.Apply(new LocalTargetInfo(worker),LocalTargetInfo.Invalid);
            if(NativePawnActivities.Owned(worker)||worker.CurJob?.def!=JobDefOf.LayDown||worker.CurJob.forceSleep!=true)
                throw new InvalidOperationException("Native imposed sleep did not release voluntary ownership.");
            var sourceThrew=false;
            try { new CompAbilityEffect_PutToSleep().Apply(new LocalTargetInfo(worker),LocalTargetInfo.Invalid); }
            catch(NullReferenceException) { sourceThrew=true; }
            if(!sourceThrew)throw new InvalidOperationException("Imposed-effect exception probe did not reach its failing native source.");
            var afterImposed=JobMaker.MakeJob(JobDefOf.Wait,40);
            worker.jobs.StartJob(afterImposed,JobCondition.InterruptForced);
            if(ReferenceEquals(worker.CurJob,afterImposed))
                throw new InvalidOperationException("Native imposed-effect permission leaked beyond its source or exception.");
        }

        // A player's ordered job and a direct native menu command each take the pawn from Hands ownership.
        private static void Player()
        {
            var playerJob=JobMaker.MakeJob(JobDefOf.Wait,400);
            if(!worker!.jobs.TryTakeOrderedJob(playerJob,JobTag.Misc))throw new InvalidOperationException("Player job rejected.");
            if(NativePawnActivities.Owned(worker)||worker.CurJob!=playerJob)throw new InvalidOperationException("Ownership blocked player takeover.");
            var authority=NativeControlAuthority.ForGame(Current.Game);
            authority.SetMode(authority.Status().Generation,NativeControlMode.Auto);
            if(!authority.Status().Active)throw new InvalidOperationException("Fixture could not restore authority for the menu boundary probe.");
            var menuJob=JobMaker.MakeJob(JobDefOf.Wait,40);
            var menu=new FloatMenuOption("Native direct command probe",()=>{
                if(authority.Status().Active)throw new InvalidOperationException("Player menu callback ran before authority revocation.");
                worker.jobs.StartJob(menuJob,JobCondition.InterruptForced);
            },playSelectionSound:false);
            menu.Chosen(true,null);
            if(authority.Status().Active||!ReferenceEquals(worker.CurJob,menuJob))
                throw new InvalidOperationException("Direct native menu command did not retain player authority.");
        }
    }
}
