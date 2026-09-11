#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Threading;
using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;
using RimWorld;
using UnityEngine;

internal static class Program
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    const string Owner="rimgovernor.ranged-causality";
    static Assembly bridge=null!, runtime=null!;
    static int checks, launchCalls, damageCalls;
    static object?[]? lastLaunch;
    static Thing? lastVictim;
    static DamageInfo lastDamage;
    static DamageWorker.DamageResult damageResult=null!;
    static Action? duringDamage, duringLaunch;
    static Exception? nativeError;
    static Type Producer=>bridge.GetType("HomeBridge.BridgeTools.NativeRangedCausality",true)!;
    static Type Authority=>runtime.GetType("HomeBridge.BridgeTools.NativeControlAuthority",true)!;
    static object Raw(Type t)=>FormatterServices.GetUninitializedObject(t);
    static T Raw<T>()=>(T)Raw(typeof(T));
    static FieldInfo Field(Type t,string n){for(Type? p=t;p!=null;p=p.BaseType){var f=p.GetField(n,F|BindingFlags.DeclaredOnly);if(f!=null)return f;}throw new MissingFieldException(t.FullName,n);}
    static void Set(object value,string n,object? data)
    {
        var field=Field(value.GetType(),n);
        // Reflection SetValue eagerly runs Projectile's graphics-only initializer.
        // An ordinary instance-field store needs no shader/material setup.
        var method=new DynamicMethod("SetFixtureField",typeof(void),new[]{typeof(object),typeof(object)},typeof(Program),true);
        var il=method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,field.DeclaringType!);il.Emit(OpCodes.Ldarg_1);
        il.Emit(field.FieldType.IsValueType?OpCodes.Unbox_Any:OpCodes.Castclass,field.FieldType);il.Emit(OpCodes.Stfld,field);il.Emit(OpCodes.Ret);
        ((Action<object,object?>)method.CreateDelegate(typeof(Action<object,object?>)))(value,data);
    }
    static object? Get(object value,string n)=>value.GetType().GetProperty(n,F)?.GetValue(value)??Field(value.GetType(),n).GetValue(value);
    static object? Call(Type t,object? value,string n,params object?[] args)=>t.GetMethod(n,F)!.Invoke(value,args);
    static MethodInfo Method(string name)=>(MethodInfo)Producer.GetField(name,F)!.GetValue(null)!;
    static bool Ready=>(bool)Producer.GetProperty("IsReady",F)!.GetValue(null)!;
    static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
    // These sinks replace only native forwarding calls, not attribution code.
    // Their results model callback state, and do not prove native combat damage.
    static bool LaunchSink(Projectile __instance, object[] __args)
    {
        launchCalls++;lastLaunch=__args;
        if(nativeError!=null)throw nativeError;
        Set(__instance,"launcher",__args[0]);__instance.usedTarget=(LocalTargetInfo)__args[2];__instance.intendedTarget=(LocalTargetInfo)__args[3];
        duringLaunch?.Invoke();return false;
    }
    static bool DamageSink(Thing __instance, DamageInfo dinfo, ref DamageWorker.DamageResult __result)
    {
        damageCalls++;lastVictim=__instance;lastDamage=dinfo;
        if(nativeError!=null)throw nativeError;
        var callback=duringDamage;duringDamage=null;callback?.Invoke();__result=damageResult;return false;
    }
    static bool MeleeSink(Verb_MeleeAttackDamage __instance, LocalTargetInfo target, ref DamageWorker.DamageResult __result)
    { __result=target.Thing.TakeDamage(new DamageInfo(null,3,instigator:__instance.CasterPawn));return false; }
    sealed class Fixture
    {
        internal readonly Game Game=Raw<Game>(); internal readonly Map Map=Raw<Map>();
        internal readonly Pawn Attacker=Raw<Pawn>(), Victim=Raw<Pawn>();
        internal readonly Job Job=Raw<Job>(); internal readonly Verb_Shoot Verb=Raw<Verb_Shoot>();
        internal readonly ThingWithComps Equipment=Raw<ThingWithComps>();internal readonly ThingDef BulletDef=Raw<ThingDef>();
        internal readonly Bullet Bullet=Raw<Bullet>();internal readonly object Control, Record;
        internal bool LaunchAllowed=true, ImpactAllowed=true;
        readonly ulong generation;readonly string lease;
        internal Fixture()
        {
            nativeError=null;duringDamage=null;duringLaunch=null;damageResult=new DamageWorker.DamageResult{totalDamageDealt=3};
            Set(Game,"maps",new List<Map>{Map});Set(Game,"currentMapIndex",(sbyte)0);
            var tick=Raw<TickManager>();Set(tick,"ticksGameInt",123);Set(Game,"tickManager",tick);Field(typeof(Current),"gameInt").SetValue(null,Game);
            var identityType=runtime.GetType("HomeBridge.BridgeTools.ColonyIdentity",true)!;
            Game.components=new List<GameComponent>{(GameComponent)Activator.CreateInstance(identityType,Game)!};
            var pawnDef=Raw<ThingDef>();pawnDef.defName="Human";int id=1;
            foreach(var pawn in new[]{Attacker,Victim}){pawn.def=pawnDef;pawn.thingIDNumber=id++;Set(pawn,"mapIndexOrState",(sbyte)0);pawn.health=Raw<Pawn_HealthTracker>();Set(pawn.health,"healthState",PawnHealthState.Mobile);}
            var attackDef=Raw<JobDef>();JobDefOf.AttackStatic=attackDef;Job.def=attackDef;Job.loadID=41;Job.targetA=new LocalTargetInfo(Victim);Job.verbToUse=Verb;
            Attacker.jobs=Raw<Pawn_JobTracker>();Attacker.jobs.curJob=Job;
            BulletDef.defName="Bullet_Test";BulletDef.thingClass=typeof(Bullet);BulletDef.projectile=new ProjectileProperties();
            Equipment.def=Raw<ThingDef>();Equipment.def.defName="TestWeapon";
            var equip=Raw<CompEquippable>();equip.parent=Equipment;var tracker=Raw<VerbTracker>();Set(tracker,"directOwner",equip);Set(Verb,"verbTracker",tracker);Set(Verb,"caster",Attacker);
            Verb.verbProps=new VerbProperties{verbClass=typeof(Verb_Shoot),ai_IsWeapon=true,range=30,defaultProjectile=BulletDef};
            Bullet.def=BulletDef;Bullet.thingIDNumber=100;Set(Bullet,"mapIndexOrState",(sbyte)0);
            Control=Call(Authority,null,"ForGame",Game)!;var status=Call(Authority,Control,"SetHookHealth",true)!;
            var acquired=Call(Authority,Control,"Acquire",Get(status,"Generation"),"test-controller",1UL,30000)!;
            Check(Get(acquired,"Error")!.ToString()=="None","actual native authority acquired for fixture");
            var snapshot=Get(acquired,"Snapshot")!;generation=(ulong)Get(snapshot,"Generation")!;lease=(string)Get(Get(snapshot,"Lease")!,"LeaseId")!;
            Check((bool)Call(Producer,null,"Supports",Verb,Attacker,Victim)!,"actual ordinary bullet verb is supported");
            Record=Call(Producer,null,"Track",Game,Attacker,Victim,Job,new Func<bool>(()=>LaunchAllowed&&Authorized()),new Func<bool>(()=>ImpactAllowed&&Authorized()))!;
        }
        bool Authorized()=>Get(Call(Authority,Control,"Check",generation,lease,"test-controller")!,"Error")!.ToString()=="None";
        internal DamageInfo Info(Thing? instigator=null)=>new DamageInfo(null,3,instigator:instigator??Attacker);
        internal void Launch(Bullet? projectile=null,Thing? launcher=null,LocalTargetInfo? target=null,LocalTargetInfo? used=null)
        {
            Call(Producer,null,"LaunchBullet",projectile??Bullet,launcher??Attacker,new Vector3(1,2,3),used??target??new LocalTargetInfo(Victim),target??new LocalTargetInfo(Victim),ProjectileHitFlags.IntendedTarget,true,Equipment,null,Verb);
        }
        internal object? Hit(Thing? target=null,Bullet? projectile=null,bool shield=false,DamageInfo? info=null)=>Call(Producer,null,"ApplyBulletDamage",target??Victim,info??Info(),projectile??Bullet,shield);
        internal object TrackAgain()=>Call(Producer,null,"Track",Game,Attacker,Victim,Job,new Func<bool>(()=>LaunchAllowed&&Authorized()),new Func<bool>(()=>ImpactAllowed&&Authorized()))!;
        internal bool Loss=>(bool)Call(Producer,null,"HasTrackingLoss",Record)!;
        internal Bullet NewBullet(int id){var bullet=Raw<Bullet>();bullet.def=BulletDef;bullet.thingIDNumber=id;Set(bullet,"mapIndexOrState",(sbyte)0);return bullet;}
        internal bool Observed=>(bool)Get(Record,"ObservedDamage")!;
        internal void Health(PawnHealthState state)=>Set(Victim.health,"healthState",state);
        internal void Manual()=>Call(Authority,Control,"RevokeExternal",Enum.Parse(runtime.GetType("HomeBridge.BridgeTools.NativeControlRevocationReason",true)!,"Manual"));
    }
    internal static void Start(string path)
    {
        bridge=Assembly.LoadFrom(Path.GetFullPath(path));foreach(var reference in bridge.GetReferencedAssemblies())Assembly.Load(reference);
        runtime=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="RimGovernor.Runtime");Run();
    }
    static List<CodeInstruction> Rewrite(string method,IEnumerable<CodeInstruction> code)=>((IEnumerable<CodeInstruction>)Call(Producer,null,method,code)!).ToList();
    static int Calls(IEnumerable<CodeInstruction> code,MethodInfo method)=>code.Count(row=>(row.opcode==OpCodes.Call||row.opcode==OpCodes.Callvirt)&&Equals(row.operand,method));
    static void Run()
    {
        Field(typeof(DefOfHelper),"bindingNow").SetValue(null,true);
        Field(typeof(UnityDataInitializer),"initializing").SetValue(null,true);
        Field(typeof(UnityData),"mainThreadId").SetValue(null,Thread.CurrentThread.ManagedThreadId);
        var shot=AccessTools.DeclaredMethod(typeof(Verb_LaunchProjectile),"TryCastShot");var impact=AccessTools.DeclaredMethod(typeof(Bullet),"Impact");
        var shotIl=PatchProcessor.GetOriginalInstructions(shot);var impactIl=PatchProcessor.GetOriginalInstructions(impact);
        Check(Calls(shotIl,Method("NativeLaunch"))==5,"actual native shot IL has five launch sites");
        Check(Calls(impactIl,Method("DamageTarget"))==2,"actual native impact IL has two direct damage sites");
        var notify=AccessTools.DeclaredMethod(typeof(Bullet),"NotifyImpact");Check(Calls(impactIl,notify)==1,"actual native impact IL has separate notification call");
        var rewritten=Rewrite("RewriteImpact",impactIl);Check(Calls(rewritten,Method("ImpactWrapper"))==2&&Calls(rewritten,Method("DamageTarget"))==0,"actual impact transpiler replaces precisely direct calls");
        Check(Calls(rewritten,notify)==1,"NotifyImpact remains outside wrappers");
        Check(Calls(Rewrite("RewriteLaunch",shotIl),Method("LaunchWrapper"))==5,"actual launch transpiler replaces five sites");
        Check(!Ready,"readiness does not install patches");Call(Producer,null,"Initialize");Check(Ready,"actual four hooks installed with verified native IL shape");
        var sink=new Harmony("rimgovernor.tests.ranged-sinks");
        sink.Patch(Method("NativeLaunch"),prefix:new HarmonyMethod(typeof(Program),nameof(LaunchSink)){priority=Priority.Last});
        sink.Patch(Method("DamageTarget"),prefix:new HarmonyMethod(typeof(Program),nameof(DamageSink)){priority=Priority.Last});
        var f=new Fixture();f.Launch(used:new LocalTargetInfo(new IntVec3(8,0,9)));Check(launchCalls==1&&lastLaunch!=null&&ReferenceEquals(lastLaunch[0],f.Attacker)&&(bool)lastLaunch[5]!,"wrapper forwards actual launch arguments once");
        Check((Vector3)lastLaunch![1]! ==new Vector3(1,2,3) && ((LocalTargetInfo)lastLaunch[2]!).Cell==new IntVec3(8,0,9) && ReferenceEquals(((LocalTargetInfo)lastLaunch[3]!).Thing,f.Victim) && (ProjectileHitFlags)lastLaunch[4]! ==ProjectileHitFlags.IntendedTarget && ReferenceEquals(lastLaunch[6],f.Equipment) && lastLaunch[7]==null,"launch forwards distinct used/intended targets, origin, flags, equipment and cover");
        duringDamage=()=>f.Health(PawnHealthState.Down);var result=f.Hit();Check(ReferenceEquals(result,damageResult)&&ReferenceEquals(lastVictim,f.Victim)&&ReferenceEquals(lastDamage.Instigator,f.Attacker),"damage wrapper preserves target info and exact result object");
        Check(f.Observed&&(bool)Get(f.Record,"CausedDowning")!,"exact projectile launch and direct damage earn transition evidence");
        foreach(var total in new[]{0f,-1f,float.NaN,float.PositiveInfinity,float.NegativeInfinity}){f=new Fixture();f.Launch();damageResult.totalDamageDealt=total;duringDamage=()=>f.Health(PawnHealthState.Dead);result=f.Hit();Check(ReferenceEquals(result,damageResult)&&!f.Observed,"invalid damage total forwarded without fabricated outcome");}
        f=new Fixture();f.Launch();damageResult=null!;Check(f.Hit()==null&&!f.Observed,"null native damage result forwarded without fabricated outcome");
        f=new Fixture();f.Hit();Check(!f.Observed,"unlaunched projectile cannot earn credit");
        f=new Fixture();f.Launch();f.Hit(shield:true);Check(!f.Observed,"shield-blocked impact has no credit");
        f=new Fixture();f.Launch();f.Hit(target:f.Attacker);Check(!f.Observed,"foreign impact target has no credit");
        f=new Fixture();f.Launch(target:new LocalTargetInfo(new IntVec3(4,0,4)));f.Hit();Check(!f.Observed,"cell-only intended target cannot inherit tracked pawn lineage");
        f=new Fixture();f.Launch();var other=Raw<Bullet>();other.def=f.BulletDef;other.thingIDNumber=f.Bullet.thingIDNumber;f.Hit(projectile:other);Check(!f.Observed,"equal projectile ID cannot substitute another object");
        f=new Fixture();f.Launch();f.Bullet.thingIDNumber++;f.Hit();Check(!f.Observed,"changed projectile ID rejects pooled reference");
        f=new Fixture();f.Job.loadID++;f.Launch();f.Hit();Check(!f.Observed,"changed job ID before launch rejects lineage");
        f=new Fixture();f.Launch();f.Attacker.jobs.curJob=null;f.LaunchAllowed=false;f.Hit();Check(f.Observed,"in-flight lineage does not require continuing launch job");
        f=new Fixture();f.LaunchAllowed=false;f.Launch();f.Hit();Check(!f.Observed,"launch guard refusal prevents lineage");
        f=new Fixture();f.Launch();f.Manual();f.Hit();Check(!f.Observed,"actual Manual authority revocation refuses delayed impact");
        f=new Fixture();f.Launch();duringDamage=f.Manual;f.Hit();Check(!f.Observed,"Manual during damage refuses result attribution");
        f=new Fixture();f.Launch();f.Victim.TakeDamage(f.Info());Check(!f.Observed,"same-instigator notification-style side damage outside direct wrapper has no credit");f.Hit();Check(f.Observed,"later audited direct call retains its separate lineage");
        f=new Fixture();f.Launch();duringDamage=()=>f.Victim.TakeDamage(f.Info());f.Hit();Check(!f.Observed,"nested native damage invalidates outer result");
        f=new Fixture();f.Launch();duringDamage=()=>f.Hit();f.Hit();Check(!f.Observed,"nested direct wrapper cannot earn credit or preserve outer result");
        f=new Fixture();f.Launch();var error=new InvalidOperationException("native damage failure");nativeError=error;try{f.Hit();throw new Exception("missing native error");}catch(TargetInvocationException e){Check(ReferenceEquals(e.InnerException,error),"native damage exception propagates unchanged");}nativeError=null;Check((int)Producer.GetField("damageDepth",F)!.GetValue(null)! ==0,"exception finalizer restores depth");f.Hit();Check(f.Observed,"subsequent direct damage works after exception cleanup");
        f=new Fixture();f.Job.loadID++;var newer=f.TrackAgain();f.Launch();f.Hit();Check(!f.Observed&&(bool)Get(newer,"ObservedDamage")!,"pooled job reference with new immutable identity creates separate record and cannot credit old job");
        f=new Fixture();f.Launch();f.Job.loadID++;newer=f.TrackAgain();f.Hit();Check(f.Observed&&!(bool)Get(newer,"ObservedDamage")!,"old flight keeps original job identity after pooled reuse");
        f=new Fixture();nativeError=error;try{f.Launch();throw new Exception("missing launch exception");}catch(TargetInvocationException e){Check(ReferenceEquals(e.InnerException,error),"native launch exception propagates unchanged");}nativeError=null;Check(f.Loss,"failed native launch marks incomplete tracking");f.Hit();Check(!f.Observed,"failed launch creates no lineage");
        f=new Fixture();duringLaunch=()=>Set(f.Bullet,"launcher",f.Victim);f.Launch();duringLaunch=null;Check(f.Loss,"partial native launch marks incomplete tracking");f.Hit();Check(!f.Observed,"mismatched native launch fields create no lineage");
        f=new Fixture();f.Bullet.def=Raw<ThingDef>();f.Launch();Check(f.Loss,"drifted projectile definition marks incomplete tracking");f.Hit();Check(!f.Observed,"drifted projectile receives no lineage");
        f=new Fixture();f.Launch();var failed=f.NewBullet(101);nativeError=error;try{f.Launch(failed);}catch(TargetInvocationException){}nativeError=null;Check(f.Loss,"later failed launch preserves loss latch");duringDamage=()=>f.Health(PawnHealthState.Dead);f.Hit();Check((bool)Get(f.Record,"CausedDeath")!,"earlier independently tracked flight can still prove terminal outcome after another launch loss");
        f=new Fixture();nativeError=error;try{f.Launch();}catch(TargetInvocationException){}nativeError=null;var later=f.NewBullet(102);f.Launch(later);duringDamage=()=>f.Health(PawnHealthState.Dead);f.Hit(projectile:later);Check(f.Loss&&(bool)Get(f.Record,"CausedDeath")!,"sticky loss does not prevent later valid independent flight evidence");
        f=new Fixture();f.Launch();var games=Producer.GetField("Games",F)!.GetValue(null)!;object?[] lookup={f.Game,null};Call(games.GetType(),games,"TryGetValue",lookup);var state=lookup[1]!;var flights=(System.Collections.IDictionary)Get(state,"Flights")!;var exemplar=flights[f.Bullet]!;
        while(flights.Count<16384)flights.Add(f.NewBullet(1000+flights.Count),exemplar);
        var overflow=f.NewBullet(20000);f.Launch(overflow);Check(f.Loss,"full launch lineage storage marks loss without evicting");f.Hit(projectile:overflow);Check(!f.Observed,"capacity overflow cannot fabricate tracked flight");f.Hit();Check(f.Observed,"full storage retains independently tracked prior flight");
        foreach(var pair in new[]{new[]{"RewriteLaunch","NativeLaunch","LaunchWrapper"},new[]{"RewriteImpact","DamageTarget","ImpactWrapper"}})
        {
            var original=(pair[0]=="RewriteLaunch"?shotIl:impactIl).Select(row=>new CodeInstruction(row)).ToList();
            var exact=Method(pair[1]);var index=original.FindIndex(row=>Equals(row.operand,exact));
            original.RemoveAt(index);var output=Rewrite(pair[0],original);Check(Calls(output,Method(pair[2]))==0&&!Ready,"missing expected call site fails shape without partial rewrite");
            var intact=pair[0]=="RewriteLaunch"?shotIl:impactIl;Rewrite(pair[0],intact);Check(Ready,"original call shape restores health evidence");
            original=intact.Select(row=>new CodeInstruction(row)).ToList();original.Add(new CodeInstruction(OpCodes.Callvirt,exact));output=Rewrite(pair[0],original);Check(Calls(output,Method(pair[2]))==0&&!Ready,"extra call site fails closed");Rewrite(pair[0],intact);
            original=intact.Select(row=>new CodeInstruction(row)).ToList();var call=original.First(row=>Equals(row.operand,exact));call.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));output=Rewrite(pair[0],original);Check(Calls(output,Method(pair[2]))==0&&!Ready,"exception boundary call site fails closed");Rewrite(pair[0],intact);
            original=intact.Select(row=>new CodeInstruction(row)).ToList();call=original.First(row=>Equals(row.operand,exact));var label=new DynamicMethod("LabelFixture",typeof(void),Type.EmptyTypes).GetILGenerator().DefineLabel();call.labels.Add(label);output=Rewrite(pair[0],original);index=output.FindIndex(row=>row.labels.Contains(label));Check(index>=0&&output[index].opcode==OpCodes.Ldarg_0,"branch label preserved on inserted receiver load");
        }
        foreach(var item in new[]{new[]{"LaunchTarget","Transpilers","LaunchTranspiler"},new[]{"ImpactTarget","Transpilers","ImpactTranspiler"},new[]{"DamageTarget","Prefixes","DamagePrefix"},new[]{"DamageTarget","Finalizers","DamageFinalizer"}})
        {
            f=new Fixture();f.Launch();new Harmony(Owner).Unpatch(Method(item[0]),Method(item[2]));Check(!Ready,"removed required "+item[2]+" fails live health");f.Hit();Check(!f.Observed,"unhealthy hook set refuses impact evidence");
            Call(Producer,null,"Initialize");Check(Ready,"partial hook set repairs");
            foreach(var registration in new[]{new[]{"LaunchTarget","Transpilers","LaunchTranspiler"},new[]{"ImpactTarget","Transpilers","ImpactTranspiler"},new[]{"DamageTarget","Prefixes","DamagePrefix"},new[]{"DamageTarget","Finalizers","DamageFinalizer"}})
            {
                var info=Harmony.GetPatchInfo(Method(registration[0]));var patches=(IEnumerable<Patch>)Get(info,registration[1])!;
                Check(patches.Count(row=>row.owner==Owner&&row.PatchMethod==Method(registration[2]))==1,"exactly one required registration after repair: "+registration[2]);
            }
            f.Hit();Check(f.Observed,"positive actual wrapper works after partial repair "+item[2]+" depth="+Producer.GetField("damageDepth",F)!.GetValue(null));
        }
        Check(Ready&&(int)Producer.GetField("damageDepth",F)!.GetValue(null)! ==0,"final live hooks and damage depth remain valid");
        var melee=bridge.GetType("HomeBridge.BridgeTools.NativeCombatCausality",true)!;
        Call(melee,null,"Initialize");
        MethodInfo MeleeMethod(string field)=>(MethodInfo)melee.GetField(field,F)!.GetValue(null)!;
        sink.Patch(MeleeMethod("MeleeTarget"),prefix:new HarmonyMethod(typeof(Program),nameof(MeleeSink)){priority=Priority.Last});
        foreach(var missing in new[]{new[]{"Target","DamageFinalizer"},new[]{"MeleeTarget","MeleeFinalizer"}})
        {
            f=new Fixture();JobDefOf.AttackMelee=Raw<JobDef>();f.Job.def=JobDefOf.AttackMelee;
            var record=Call(melee,null,"Track",f.Game,f.Attacker,f.Victim,f.Job,new Func<bool>(()=>true))!;
            var verb=Raw<Verb_MeleeAttackDamage>();Set(verb,"caster",f.Attacker);
            new Harmony("rimgovernor.combat-causality").Unpatch(MeleeMethod(missing[0]),MeleeMethod(missing[1]));
            Check(!(bool)melee.GetProperty("IsReady",F)!.GetValue(null)!,"adjacent melee missing cleanup hook fails health");
            var priorCalls=damageCalls;MeleeMethod("MeleeTarget").Invoke(verb,new object[]{new LocalTargetInfo(f.Victim)});
            Check(damageCalls==priorCalls+1&&!(bool)Get(record,"ObservedDamage")!,"unhealthy melee still forwards native damage without credit");
            Check((int)melee.GetField("damageDepth",F)!.GetValue(null)! ==0&&melee.GetField("meleeScope",F)!.GetValue(null)==null,"unhealthy melee leaves no unpaired damage depth or verb scope");
            Call(melee,null,"Initialize");
            foreach(var registration in new[]{new[]{"Target","Prefixes","Prefix"},new[]{"Target","Postfixes","Postfix"},new[]{"Target","Finalizers","DamageFinalizer"},new[]{"MeleeTarget","Prefixes","MeleePrefix"},new[]{"MeleeTarget","Finalizers","MeleeFinalizer"}})
            {
                var info=Harmony.GetPatchInfo(MeleeMethod(registration[0]));var patches=(IEnumerable<Patch>)Get(info,registration[1])!;
                Check(patches.Count(row=>row.owner=="rimgovernor.combat-causality"&&row.PatchMethod==MeleeMethod(registration[2]))==1,"adjacent melee exact registration after repair "+registration[2]);
            }
            MeleeMethod("MeleeTarget").Invoke(verb,new object[]{new LocalTargetInfo(f.Victim)});Check((bool)Get(record,"ObservedDamage")!,"actual melee dispatch works after missing cleanup repair");
            Check((int)melee.GetField("damageDepth",F)!.GetValue(null)! ==0&&melee.GetField("meleeScope",F)!.GetValue(null)==null,"repaired actual melee dispatch cleans scopes");
        }
        Console.WriteLine(checks+" actual-assembly ranged checks passed; synthetic forwarding sinks, not gameplay damage acceptance.");
    }
}


internal static class Bootstrap
{
    static int Main(string[] args)
    {
        try
        {
            var dirs=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
            AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=dirs.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return path==null?null:Assembly.LoadFrom(path);};
            typeof(Bootstrap).Assembly.GetType("Program",true)!.GetMethod("Start",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{args[0]});return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
