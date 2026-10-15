#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using Common = RimGovernor.Contracts.Wire.Common;
namespace HomeBridge.BridgeTools
{
    public sealed partial class PawnControlFixture
    {
        private static Pawn? worker, helper;
        private static Thing? crop, filth, food, bed;
        private static long deadline;
        private static Common.ObservationContext Context()
        {
            var s=NativeControlAuthority.ForGame(Current.Game).Status();
            return new Common.ObservationContext { NativeGeneration=s.Generation, Tick=Find.TickManager.TicksGame,
                Identity=new Common.Identity { ColonyId=s.Identity!.ColonyId, LoadToken=s.Identity.LoadToken, MapId=s.Identity.MapId } };
        }
        [Tool("test/pawn_control_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare two-pawn controlled work, needs and dependency fixture.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map=Find.CurrentMap;
            worker=map.mapPawns.FreeColonistsSpawned.First(p=>!p.WorkTypeIsDisabled(WorkTypeDefOf.Growing)&&!p.WorkTagIsDisabled(WorkTags.Firefighting));
            helper=map.mapPawns.FreeColonistsSpawned.First(p=>p!=worker && !p.WorkTypeIsDisabled(WorkTypeDefOf.Cleaning));
            foreach(var p in map.mapPawns.FreeColonistsSpawned.ToArray()) p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait,120000),JobCondition.InterruptForced);
            var cell=GenRadial.RadialCellsAround(worker.Position,8,false).First(c=>c.InBounds(map)&&c.Standable(map)&&c.GetEdifice(map)==null&&c.DistanceTo(worker.Position)>4);
            foreach(var p in cell.GetThingList(map).OfType<Plant>().ToArray()) p.Destroy();
            crop=GenSpawn.Spawn(DefDatabase<ThingDef>.AllDefsListForReading.First(d=>d.plant?.Sowable==true&&d.plant.harvestedThingDef?.IsNutritionGivingIngestible==true),cell,map);
            ((Plant)crop).Growth=1;
            filth=GenSpawn.Spawn(ThingDefOf.Filth_Dirt,cell,map);
            // Native clean eligibility uses the filth age, not merely its existence.
            (AccessTools.Field(typeof(Filth),"growTick") ?? throw new MissingFieldException("Filth.growTick")).SetValue(filth, -10000);
            map.areaManager.Home[cell]=true;
            food=GenSpawn.Spawn(ThingDefOf.MealSimple,worker.Position,map); food.stackCount=1;
            var bedCell=GenRadial.RadialCellsAround(worker.Position,8,false).First(c=>c.InBounds(map)&&c.Standable(map)&&c.GetEdifice(map)==null&&c!=cell&&c.DistanceTo(worker.Position)>2);
            bed=GenSpawn.Spawn(ThingDefOf.Bed,bedCell,map);
            worker.needs.food.CurLevelPercentage=.1f; worker.needs.rest.CurLevelPercentage=.1f;
            worker.skills.GetSkill(SkillDefOf.Plants).Level=20;
            deadline=Find.TickManager.TicksGame+10000;
            return new {success=true,worker=worker.GetUniqueLoadID(),helper=helper.GetUniqueLoadID()};
        },ct).ConfigureAwait(false);

        [Tool("test/pawn_control_install",Description="UNSAFE FOR MODEL EXECUTION. Exercise production assignment validation/installation in the prepared lab.")]
        public async Task<object> Install(IRimBridgeContext ctx,CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(()=>
        {
            var c=Context();
            var clean=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.Worker is WorkGiver_CleanFilth).defName;
            var harvest=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.Worker is WorkGiver_GrowerHarvest).defName;
            var batch=new PawnActivities(c.NativeGeneration!.Value,new[]{
                new PawnActivityOrder(worker!.GetUniqueLoadID(),"producer","food-method",0,1,false,new[]{
                    new PawnActivityStep(PawnActivityKind.Hold,"","","",0,deadline,filth!.GetUniqueLoadID()),
                    new PawnActivityStep(PawnActivityKind.Work,crop!.GetUniqueLoadID(),harvest,"Harvest",0,deadline),
                    new PawnActivityStep(PawnActivityKind.Eat,food!.GetUniqueLoadID(),"","",1,deadline),
                    new PawnActivityStep(PawnActivityKind.Sleep,bed!.GetUniqueLoadID(),"","",0,deadline)}),
                new PawnActivityOrder(helper!.GetUniqueLoadID(),"helper","food-method",0,1,false,new[]{
                    new PawnActivityStep(PawnActivityKind.Work,filth!.GetUniqueLoadID(),clean,"Clean",0,deadline),
                    new PawnActivityStep(PawnActivityKind.Hold,"","","",0,deadline)})});
            batch = batch with { Orders = batch.Orders.Concat(worker!.Map.mapPawns.FreeColonistsSpawned
                .Where(p => p != worker && p != helper).Select(p => new PawnActivityOrder(p.GetUniqueLoadID(),
                    "fixture-idle-" + p.GetUniqueLoadID(), "food-method", 0, 1, false,
                    [new(PawnActivityKind.Idle, "", "", "", 0, deadline)]))).ToArray() };
            var invalid=NativePawnActivities.Validate(batch,c); if(invalid!=null)throw new InvalidOperationException(invalid.Detail);
            NativePawnActivities.Apply(batch,c);
            NativePawnActivities.Apply(batch,c); // Idempotent reinstall must not restart work.
            var before=NativePawnActivities.Read(worker);
            for(var i=0;i<8;i++)worker.jobs.CheckForJobOverride();
            if(NativePawnActivities.Read(worker)!=before)throw new InvalidOperationException("Override probes consumed an assignment.");
            var stale=batch with { Orders=new[]{batch.Orders[0] with { Revision=2,ExpectedRevision=0 }} };
            if(NativePawnActivities.Validate(stale,c)==null)throw new InvalidOperationException("Stale revision accepted.");
            return new {success=true};
        },ct).ConfigureAwait(false);

        [Tool("test/pawn_control_read",Description="UNSAFE FOR MODEL EXECUTION. Read actual controlled work/needs and lifecycle evidence.")]
        public async Task<object> Read(IRimBridgeContext ctx,CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(()=>new {
            success=true,worker=NativePawnActivities.Read(worker!),helper=NativePawnActivities.Read(helper!),
            job=worker!.CurJob?.def.defName,food=worker.needs.food.CurLevelPercentage,rest=worker.needs.rest.CurLevelPercentage,
            filthGone=filth!.Destroyed, cropHarvested=((Plant)crop!).Growth<1 || crop.Destroyed,
            forced=worker.CurJob?.playerForced,tick=Find.TickManager.TicksGame
        },ct).ConfigureAwait(false);

        [Tool("test/pawn_control_release",Description="UNSAFE FOR MODEL EXECUTION. Verify authority revocation releases owned execution without replacing unrelated work.")]
        public async Task<object> Release(IRimBridgeContext ctx,CancellationToken ct)=>await ctx.MainThread.InvokeAsync<object>(()=>
        {
            var auth=NativeControlAuthority.ForGame(Current.Game); var s=auth.Status();
            auth.Revoke(s.Generation,NativeControlRevocationReason.Manual);
            if(NativePawnActivities.Read(worker!)!=null||NativePawnActivities.Read(helper!)!=null)throw new InvalidOperationException("Revocation retained activity ownership.");
            return new {success=true};
        },ct).ConfigureAwait(false);
        [Tool("test/pawn_control_loaded",Description="UNSAFE FOR MODEL EXECUTION. Assert a real reload discarded all old execution ownership.")]
        public async Task<object> Loaded(IRimBridgeContext ctx,CancellationToken ct)=>await ctx.MainThread.InvokeAsync<object>(()=>
        {
            if(Find.CurrentMap.mapPawns.AllPawnsSpawned.Any(NativePawnActivities.Owned) || (worker!=null && NativePawnActivities.Owned(worker)))
                throw new InvalidOperationException("Load restored activity ownership.");
            return new {success=true};
        },ct).ConfigureAwait(false);

        [Tool("test/pawn_control_lifecycle",Description="UNSAFE FOR MODEL EXECUTION. Bounded native deadline, refusal, atomicity and player-takeover probes.")]
        public async Task<object> Lifecycle(IRimBridgeContext ctx,CancellationToken ct)=>await ctx.MainThread.InvokeAsync<object>(()=>
        {
            var c=Context();
            var original=NativePawnActivities.Read(worker!)!;
            var invalid=new PawnActivities(c.NativeGeneration!.Value,new[]{
                new PawnActivityOrder(worker!.GetUniqueLoadID(),"producer","food-method",1,2,false,new[]{new PawnActivityStep(PawnActivityKind.Hold,"","","",0,deadline)}),
                new PawnActivityOrder(helper!.GetUniqueLoadID(),"helper","food-method",1,2,false,new[]{new PawnActivityStep(PawnActivityKind.Eat,"missing","","",1,deadline)})});
            if(NativePawnActivities.Validate(invalid,c)==null || NativePawnActivities.Read(worker)!=original)throw new InvalidOperationException("Invalid batch was not atomic.");
            var bounded=new PawnActivities(c.NativeGeneration.Value,new[]{invalid.Orders[0] with {Task="terminal-fixture",Steps=new[]{new PawnActivityStep(PawnActivityKind.Hold,"","","",0,Find.TickManager.TicksGame+2)}}});
            NativePawnActivities.Apply(bounded,c);
            var choose=AccessTools.Method(typeof(Pawn_JobTracker),"DetermineNextJob");
            var offer=(ThinkResult)(choose.Invoke(worker.jobs,new object?[]{null,false}) ?? throw new InvalidOperationException("Missing think result"));
            if(!offer.IsValid)throw new InvalidOperationException("Hold offer missing.");
            worker.jobs.StartJob(offer.Job,JobCondition.InterruptForced,offer.SourceNode);
            for(var i=0;i<3;i++)Find.TickManager.DoSingleTick();
            if(NativePawnActivities.Read(worker)?.Reason!="hold_deadline")throw new InvalidOperationException("Hold deadline did not terminate authorization.");
            Find.TickManager.CurTimeSpeed=TimeSpeed.Normal;
            NativePawnActivities.WaitTick(worker);
            if(!Find.TickManager.Paused)throw new InvalidOperationException("Failed task-backed hold allowed time without continuation.");
            var repair=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.Worker is WorkGiver_Repair).defName;
            var impossible=bounded with {Orders=new[]{bounded.Orders[0] with {ExpectedRevision=2,Revision=3,Steps=new[]{new PawnActivityStep(PawnActivityKind.Work,bed!.GetUniqueLoadID(),repair,"Repair",0,deadline)}}}};
            NativePawnActivities.Apply(impossible,Context());
            _=choose.Invoke(worker.jobs,new object?[]{null,false});
            if(NativePawnActivities.Read(worker)?.State!="failed")throw new InvalidOperationException("Native work refusal was hidden.");
            var pinCell=GenRadial.RadialCellsAround(worker.Position,10,false).First(x=>x.InBounds(worker.Map)&&x.Standable(worker.Map)&&x.GetEdifice(worker.Map)==null&&x.DistanceTo(worker.Position)>4);
            var pin=GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("HorseshoesPin"),pinCell,worker.Map);
            var recreation=impossible with { Orders=new[]{impossible.Orders[0] with {ExpectedRevision=3,Revision=4,Steps=new[]{new PawnActivityStep(PawnActivityKind.Recreation,pin.GetUniqueLoadID(),"","",0,deadline)}}}};
            NativePawnActivities.Apply(recreation,Context());
            var joyOffer=(ThinkResult)choose.Invoke(worker.jobs,new object?[]{null,false})!;
            if(!joyOffer.IsValid)throw new InvalidOperationException("Recreation offer missing.");
            worker.jobs.StartJob(joyOffer.Job,JobCondition.None,joyOffer.SourceNode);
            if(worker.CurJob?.def.defName!="Play_Horseshoes" || worker.CurJob.playerForced)throw new InvalidOperationException("Selected recreation did not start normally.");
            var fireCell=GenRadial.RadialCellsAround(worker.Position,8,false).First(x=>x.InBounds(worker.Map)&&x.Standable(worker.Map)&&x.GetEdifice(worker.Map)==null&&x.DistanceTo(worker.Position)>3);
            var fire=GenSpawn.Spawn(ThingDefOf.Fire,fireCell,worker.Map);
            worker.Map.areaManager.Home[fireCell]=true;
            var fireGiver=DefDatabase<WorkGiverDef>.AllDefsListForReading.First(d=>d.giverClass.FullName=="RimWorld.WorkGiver_FightFires").defName;
            var emergency=recreation with { Orders=new[]{recreation.Orders[0] with {ExpectedRevision=4,Revision=5,Steps=new[]{new PawnActivityStep(PawnActivityKind.Work,fire.GetUniqueLoadID(),fireGiver,"BeatFire",0,deadline)}}}};
            NativePawnActivities.Apply(emergency,Context());
            var fireOffer=(ThinkResult)choose.Invoke(worker.jobs,new object?[]{null,false})!;
            if(!fireOffer.IsValid)throw new InvalidOperationException("Emergency offer missing.");
            worker.jobs.StartJob(fireOffer.Job,JobCondition.None,fireOffer.SourceNode);
            if(worker.CurJob?.def!=JobDefOf.BeatFire || worker.CurJob.playerForced)throw new InvalidOperationException("Emergency did not preempt recreation.");
            var release=emergency with {Orders=new[]{emergency.Orders[0] with {ExpectedRevision=5,Revision=6,Release=true,Steps=Array.Empty<PawnActivityStep>()}}};
            NativePawnActivities.Apply(release,Context());
            NativePawnActivities.Apply(release,Context());
            if(NativePawnActivities.Owned(worker)||NativePawnActivities.Read(worker)!=null)throw new InvalidOperationException("Repeated release retained ownership.");
            var withoutOwner = (ThinkResult)choose.Invoke(worker.jobs, new object?[] { null, false })!;
            if (withoutOwner.IsValid || !Find.TickManager.Paused)
                throw new InvalidOperationException("Unassigned controlled pawn fell through to native voluntary selection.");
            var direct = JobMaker.MakeJob(JobDefOf.Wait, 40);
            worker.jobs.StartJob(direct, JobCondition.InterruptForced);
            if (ReferenceEquals(worker.CurJob, direct)) throw new InvalidOperationException("Unowned direct StartJob bypassed voluntary control.");
            var admitted = JobMaker.MakeJob(JobDefOf.Wait, 40);
            using (NativeControlAuthority.ForGame(Current.Game).Owned()) worker.jobs.StartJob(admitted, JobCondition.InterruptForced);
            if (!ReferenceEquals(worker.CurJob, admitted)) throw new InvalidOperationException("Admitted Hands scope could not start an exact job.");
            var reacquire=bounded with {Orders=new[]{bounded.Orders[0] with {Assignment="fresh-assignment",ExpectedRevision=0,Revision=1,Steps=new[]{new PawnActivityStep(PawnActivityKind.Hold,"","","",0,deadline)}}}};
            NativePawnActivities.Apply(reacquire,Context());
            var collidingRelease=release with {Orders=new[]{release.Orders[0] with {ExpectedRevision=1,Revision=2}}};
            if(NativePawnActivities.Validate(collidingRelease,Context())==null)throw new InvalidOperationException("Release matched revision but ignored changed assignment.");
            if(NativePawnActivities.Validate(release,Context())==null)throw new InvalidOperationException("Old release could erase a newer assignment.");
            var exhaustion = JobMaker.MakeJob(JobDefOf.LayDown, worker.Position);
            exhaustion.startInvoluntarySleep = true;
            worker.jobs.StartJob(exhaustion, JobCondition.InterruptForced, preToilReservationsCanFail: true);
            if (NativePawnActivities.Owned(worker) || !ReferenceEquals(worker.CurJob, exhaustion))
                throw new InvalidOperationException("Native involuntary sleep did not release voluntary ownership.");
            NativePawnActivities.Apply(reacquire, Context());
            var vomiting=JobMaker.MakeJob(JobDefOf.Vomit);
            worker.jobs.StartJob(vomiting,JobCondition.InterruptForced,resumeCurJobAfterwards:true);
            if(NativePawnActivities.Owned(worker)||!ReferenceEquals(worker.CurJob,vomiting))
                throw new InvalidOperationException("Native physiological vomiting did not release voluntary ownership.");
            // Native resumption can queue the interrupted hold; remove that fixture artifact
            // before acquiring the next independent probe.
            worker.jobs.ClearQueuedJobs();
            NativePawnActivities.Apply(reacquire,Context());
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
            worker.needs.rest.CurLevelPercentage=.1f;
            NativePawnActivities.Apply(reacquire,Context());
            var terminal=reacquire with {Orders=new[]{reacquire.Orders[0] with {ExpectedRevision=1,Revision=2,Task="terminal-fixture",
                Steps=new[]{new PawnActivityStep(PawnActivityKind.Idle,"","","",0,Find.TickManager.TicksGame+1)}}}};
            NativePawnActivities.Apply(terminal,Context());
            var idleOffer=(ThinkResult)choose.Invoke(worker.jobs,new object?[]{null,false})!;
            worker.jobs.StartJob(idleOffer.Job,JobCondition.InterruptForced,idleOffer.SourceNode);
            Find.TickManager.DoSingleTick();
            NativePawnActivities.WaitTick(worker);
            if(NativePawnActivities.Read(worker)?.State!="completed")throw new InvalidOperationException("Bounded idle did not complete.");
            Find.TickManager.CurTimeSpeed=TimeSpeed.Normal;
            NativePawnActivities.WaitTick(worker);
            if(!Find.TickManager.Paused)throw new InvalidOperationException("Completed task-backed hold allowed time without continuation.");
            var playerJob=JobMaker.MakeJob(JobDefOf.Wait,400);
            if(!worker.jobs.TryTakeOrderedJob(playerJob,JobTag.Misc))throw new InvalidOperationException("Player job rejected.");
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
            return new {success=true,deadline=true,atomic=true,refusal=true,recreation=true,emergency=true,player=true};
        },ct).ConfigureAwait(false);
    }
}
