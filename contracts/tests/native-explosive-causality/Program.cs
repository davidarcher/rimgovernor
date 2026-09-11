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
    static Action? duringDamage, duringLaunch, duringStart, duringFactory;
    static Exception? factoryError,startError,cellError;
    static object?[]? lastFactory;
    static Explosion? lastExplosion;
    static Fixture? activeFixture;
    static bool skipStart;
    static readonly List<Explosion> createdExplosions=new List<Explosion>();
    static Action? earlyFactory;
    static Func<DamageInfo,DamageInfo>? replacement;
    static int explosionId=200;
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
    static void CaptureFactoryArgs(object[] args)=>lastFactory=args;
    static IEnumerable<CodeInstruction> RecordFactoryArgs(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var code=new List<CodeInstruction>{new CodeInstruction(OpCodes.Ldc_I4,36),new CodeInstruction(OpCodes.Newarr,typeof(object))};
        var parameters=__originalMethod.GetParameters();
        for(var i=0;i<36;i++) {code.Add(new CodeInstruction(OpCodes.Dup));code.Add(new CodeInstruction(OpCodes.Ldc_I4,i));code.Add(new CodeInstruction(OpCodes.Ldarg,i));if(parameters[i].ParameterType.IsValueType)code.Add(new CodeInstruction(OpCodes.Box,parameters[i].ParameterType));code.Add(new CodeInstruction(OpCodes.Stelem_Ref));}
        code.Add(new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(Program),nameof(CaptureFactoryArgs))));code.AddRange(instructions);return code;
    }
    static bool SpawnSink(ThingDef __0,IntVec3 __1,Map __2,ref Thing __result)
    {
        if(factoryError!=null)throw factoryError;
        var nested=duringFactory;duringFactory=null;nested?.Invoke();
        var explosion=Raw<Explosion>();explosion.def=__0;explosion.thingIDNumber=explosionId++;Set(explosion,"mapIndexOrState",skipStart?(sbyte)-1:(sbyte)0);Set(explosion,"positionInt",__1);
        createdExplosions.Add(explosion);lastExplosion=explosion;__result=explosion;return false;
    }
    static bool LosSink(ref IntVec3? __3,ref IntVec3? __4){__3=null;__4=null;return false;}
    static void EarlyFactoryPrefix(){var callback=earlyFactory;earlyFactory=null;callback?.Invoke();}
    static void SubstituteDamage(ref DamageInfo dinfo){if(replacement!=null)dinfo=replacement(dinfo);}
    static void SubstituteArgs(object[] __args){if(replacement!=null)__args[0]=replacement((DamageInfo)__args[0]);}
    static bool StartSink(Explosion __instance,SoundDef explosionSound,List<Thing> ignoredThings)
    {Set(__instance,"ignoredThings",ignoredThings);if(startError!=null)throw startError;duringStart?.Invoke();return false;}
    static bool CellSink(){if(cellError!=null)throw cellError;return false;}
    static bool ExplodeSink(Projectile_Explosive __instance){Check(ReferenceEquals(__instance,activeFixture!.Bullet),"native shield impact reaches exact projectile Explode");activeFixture.Explode();return false;}
    sealed class Fixture
    {
        internal readonly Game Game=Raw<Game>(); internal readonly Map Map=Raw<Map>();
        internal readonly Pawn Attacker=Raw<Pawn>(), Victim=Raw<Pawn>();
        internal readonly Job Job=Raw<Job>(); internal readonly Verb_Shoot Verb=Raw<Verb_Shoot>();
        internal readonly ThingWithComps Equipment=Raw<ThingWithComps>();internal readonly ThingDef BulletDef=Raw<ThingDef>();
        internal readonly Projectile_Explosive Bullet=Raw<Projectile_Explosive>();
        internal readonly DamageDef Damage=Raw<DamageDef>();internal readonly object Control, Record;
        internal bool LaunchAllowed=true, ImpactAllowed=true;
        readonly ulong generation;readonly string lease;
        internal Fixture()
        {
            activeFixture=this;createdExplosions.Clear();earlyFactory=null;replacement=null;skipStart=false;factoryError=null;startError=null;cellError=null;duringStart=null;duringFactory=null;nativeError=null;duringDamage=null;duringLaunch=null;damageResult=new DamageWorker.DamageResult{totalDamageDealt=3};
            Set(Game,"maps",new List<Map>{Map});Set(Game,"currentMapIndex",(sbyte)0);
            var tick=Raw<TickManager>();Set(tick,"ticksGameInt",123);Set(Game,"tickManager",tick);Field(typeof(Current),"gameInt").SetValue(null,Game);
            var identityType=runtime.GetType("HomeBridge.BridgeTools.ColonyIdentity",true)!;
            Game.components=new List<GameComponent>{(GameComponent)Activator.CreateInstance(identityType,Game)!};
            var pawnDef=Raw<ThingDef>();pawnDef.defName="Human";int id=1;
            foreach(var pawn in new[]{Attacker,Victim}){pawn.def=pawnDef;pawn.thingIDNumber=id++;Set(pawn,"mapIndexOrState",(sbyte)0);pawn.health=Raw<Pawn_HealthTracker>();Set(pawn.health,"healthState",PawnHealthState.Mobile);}
            var attackDef=Raw<JobDef>();JobDefOf.AttackStatic=attackDef;Job.def=attackDef;Job.loadID=41;Job.targetA=new LocalTargetInfo(Victim);Job.verbToUse=Verb;
            Attacker.jobs=Raw<Pawn_JobTracker>();Attacker.jobs.curJob=Job;
            BulletDef.defName="Bullet_Test";BulletDef.thingClass=typeof(Projectile_Explosive);BulletDef.projectile=new ProjectileProperties{explosionRadius=3.25f,damageDef=Damage};Damage.defName="TestBlast";Damage.workerClass=typeof(DamageWorker_AddInjury);
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
        internal void Launch(Projectile_Explosive? projectile=null,Thing? launcher=null,LocalTargetInfo? target=null,LocalTargetInfo? used=null)
        {
            Call(Producer,null,"LaunchBullet",projectile??Bullet,launcher??Attacker,new Vector3(1,2,3),used??target??new LocalTargetInfo(Victim),target??new LocalTargetInfo(Victim),ProjectileHitFlags.IntendedTarget,true,Equipment,null,Verb);
        }
        internal object? Hit(Explosion explosion,Thing? target=null,DamageInfo? info=null)=>Call(Producer,null,"ApplyExplosionDamage",target??Victim,info??new DamageInfo(Damage,3,instigator:Attacker),explosion,Damage.Worker);
        internal object?[] Args(bool exotic=false)
        {var args=new object?[]{new IntVec3(2,0,3),Map,3.25f,Damage,Attacker,17,0.6f,Raw<SoundDef>(),Equipment.def,BulletDef,Victim,Raw<ThingDef>(),0.2f,2,(GasType?)((GasType)1),(float?)0.8f,200,true,Raw<ThingDef>(),0.3f,4,0.4f,true,(float?)45,new List<Thing>(),(FloatRange?)new FloatRange(10,20),false,2f,0.5f,false,Raw<ThingDef>(),0.8f,Raw<SimpleCurve>(),new List<IntVec3>{new IntVec3(2,0,3)},Raw<ThingDef>(),Raw<ThingDef>()};
            if(!exotic){foreach(var index in new[]{11,14,18,30,34,35})args[index]=null;args[21]=0f;}return args;
        }
        internal void Explode(object?[]? args=null,Projectile_Explosive? source=null)=>Call(Producer,null,"DoExplosionFromProjectile",(args??Args()).Concat(new object?[]{source??Bullet}).ToArray());
        internal object TrackAgain()=>Call(Producer,null,"Track",Game,Attacker,Victim,Job,new Func<bool>(()=>LaunchAllowed&&Authorized()),new Func<bool>(()=>ImpactAllowed&&Authorized()))!;
        internal bool Loss=>(bool)Call(Producer,null,"HasTrackingLoss",Record)!;
        internal Projectile_Explosive NewBullet(int id){var bullet=Raw<Projectile_Explosive>();bullet.def=BulletDef;bullet.thingIDNumber=id;Set(bullet,"mapIndexOrState",(sbyte)0);return bullet;}
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
        var factory=typeof(GenExplosion).GetMethods(F).Single(m=>m.Name=="DoExplosion"&&m.GetParameters().Length==36);
        var explode=AccessTools.DeclaredMethod(typeof(Projectile_Explosive),"Explode");
        var start=AccessTools.DeclaredMethod(typeof(Explosion),"StartExplosion");
        var damage=AccessTools.DeclaredMethod(typeof(DamageWorker),"ExplosionDamageThing");
        var take=AccessTools.DeclaredMethod(typeof(Thing),"TakeDamage");
        Check(Calls(PatchProcessor.GetOriginalInstructions(explode),factory)==1,"actual explosive projectile has one exact36argument factory site");
        Check(Calls(PatchProcessor.GetOriginalInstructions(factory),start)==1,"actual factory has one StartExplosion site");
        Check(Calls(PatchProcessor.GetOriginalInstructions(damage),take)==1,"actual base explosion worker has one direct damage site");
        Check(factory.GetParameters().Length==36,"native factory exact36parameter signature");
        Check(Method("ExplodeWrapper").GetParameters().Take(36).Select(p=>p.ParameterType).SequenceEqual(factory.GetParameters().Select(p=>p.ParameterType)),"wrapper preserves native36parameter types in order");
        Check(!Ready,"readiness does not install hooks");Call(Producer,null,"Initialize");Check((bool)Producer.GetProperty("ExplosiveIsReady",F)!.GetValue(null)!,"actual explosion hooks install with verified IL shape");
        var sink=new Harmony("rimgovernor.tests.explosion-sinks");
        sink.Patch(Method("NativeLaunch"),prefix:new HarmonyMethod(typeof(Program),nameof(LaunchSink)){priority=Priority.Last});
        sink.Patch(Method("DamageTarget"),prefix:new HarmonyMethod(typeof(Program),nameof(DamageSink)){priority=Priority.Last});
        sink.Patch(factory,transpiler:new HarmonyMethod(typeof(Program),nameof(RecordFactoryArgs)){priority=Priority.Last});
        var spawn=PatchProcessor.GetOriginalInstructions(factory).Select(row=>row.operand).OfType<MethodInfo>().Single(method=>method.DeclaringType==typeof(GenSpawn)&&method.Name=="Spawn");
        sink.Patch(spawn,prefix:new HarmonyMethod(typeof(Program),nameof(SpawnSink)){priority=Priority.Last});
        sink.Patch(AccessTools.DeclaredMethod(typeof(GenExplosion),"CalculateNeededLOSToCells"),prefix:new HarmonyMethod(typeof(Program),nameof(LosSink)){priority=Priority.Last});
        ThingDefOf.Explosion=Raw<ThingDef>();ThingDefOf.Explosion.defName="Explosion";
        sink.Patch(start,prefix:new HarmonyMethod(typeof(Program),nameof(StartSink)){priority=Priority.Last});
        sink.Patch(Method("ExplosionCellTarget"),prefix:new HarmonyMethod(typeof(Program),nameof(CellSink)){priority=Priority.Last});
        var f=new Fixture();f.Launch();var args=f.Args(true);f.Explode(args);Check(lastFactory!=null&&lastFactory.Length==36,"factory receives all36args");
        for(var i=0;i<36;i++)Check(Equals(args[i],lastFactory![i]),"exact native factory forwarding arg"+i);
        Check(f.Loss,"unsupported secondary payload is forwarded with tracking loss");
        f=new Fixture();f.Launch();f.Explode();var blast=lastExplosion!;duringDamage=()=>f.Health(PawnHealthState.Down);var result=f.Hit(blast);Check(f.Observed&&(bool)Get(f.Record,"CausedDowning")!,"exact later explosion damage attributed");Check(ReferenceEquals(result,damageResult)&&ReferenceEquals(lastVictim,f.Victim),"actual damage wrapper preserves result and victim");
        f=new Fixture();f.Launch();duringStart=()=>f.Hit(lastExplosion!);f.Explode();Check(!f.Observed,"Notify_Explosion-time direct side damage cannot inherit unstarted blast");duringStart=null;f.Hit(lastExplosion!);Check(f.Observed,"completed native start enables later direct cell damage");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;f.Victim.TakeDamage(new DamageInfo(f.Damage,3,instigator:f.Attacker));Check(!f.Observed,"Notify_Explosion or RectTrigger same-instigator side damage outside worker wrapper has no credit");f.Hit(blast);Check(f.Observed,"independent later direct worker call remains valid");
        f=new Fixture();f.Launch();duringFactory=()=>factory.Invoke(null,f.Args());f.Explode();var nestedBlast=createdExplosions[0];blast=lastExplosion!;f.Hit(nestedBlast);Check(!f.Observed,"nested unrelated factory cannot bind outer ticket");Check(!f.Loss,"nested unrelated native factory does not steal outer ticket");f.Hit(blast);Check(f.Observed,"outer factory binds its own exact explosion after nested factory");
        f=new Fixture();f.Launch();Set(f.Bullet,"mapIndexOrState",(sbyte)-2);Check(f.Bullet.Destroyed,"fixture reproduces destroyed source projectile");f.Explode();blast=lastExplosion!;f.Attacker.jobs.curJob=null;f.Bullet.thingIDNumber++;f.Hit(blast);Check(f.Observed,"delayed explosion uses its own identity after projectile destruction and job cleanup");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;blast.thingIDNumber++;f.Hit(blast);Check(!f.Observed,"pooled explosion identity rejected");
        f=new Fixture();f.Launch();f.Explode();f.Hit(lastExplosion!,target:f.Attacker);Check(!f.Observed,"foreign victim refused");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;damage.Invoke(f.Damage.Worker,new object[]{blast,f.Victim,new List<Thing>(),new List<Thing>{f.Victim},new IntVec3(2,0,3)});Check(!f.Observed,"actual native worker rejects ignored target before direct call");
        f=new Fixture();f.Launch();f.Explode();f.Manual();f.Hit(lastExplosion!);Check(!f.Observed,"Manual before delayed cell damage refuses attribution");
        f=new Fixture();f.Launch();f.Explode();duringDamage=f.Manual;f.Hit(lastExplosion!);Check(!f.Observed,"Manual during damage refuses result attribution");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;duringDamage=()=>f.Hit(blast);f.Hit(blast);Check(!f.Observed,"nested direct worker damage invalidates outer attribution");
        f=new Fixture();f.Launch();skipStart=true;f.Explode();Check(f.Loss,"unspawned partial factory result reports lineage loss");
        f=new Fixture();f.Launch();var error=new InvalidOperationException("original factory failure");factoryError=error;try{f.Explode();throw new Exception("missing factory exception");}catch(TargetInvocationException e){Check(ReferenceEquals(e.InnerException,error),"factory exception forwarded unchanged");}factoryError=null;Check(f.Loss,"factory failure latches tracking loss");Check((int)Producer.GetField("explosionDepth",F)!.GetValue(null)! ==0&&Producer.GetField("explosionTicket",F)!.GetValue(null)==null,"factory exception cleans depth and ticket");
        f=new Fixture();f.Launch();startError=error;try{f.Explode();throw new Exception("missing start exception");}catch(TargetInvocationException e){Check(ReferenceEquals(e.InnerException,error)||ReferenceEquals(e.InnerException?.InnerException,error),"native start exception forwarded unchanged");}startError=null;Check(f.Loss,"start failure marks incomplete lineage");f.Hit(lastExplosion!);Check(!f.Observed,"failed start cannot earn later outcome");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;cellError=error;try{Method("ExplosionCellTarget").Invoke(blast,new object[]{new IntVec3(2,0,3)});}catch(TargetInvocationException e){Check(ReferenceEquals(e.InnerException,error),"cell failure retains original exception");}cellError=null;Check(f.Loss,"native cell failure reports incomplete propagation");f.Hit(blast);Check(f.Observed,"independent later valid cell remains observable after prior failure");
        f=new Fixture();f.Launch();sink.Patch(Method("ExplodeTarget"),prefix:new HarmonyMethod(typeof(Program),nameof(ExplodeSink)){priority=Priority.Last});AccessTools.DeclaredMethod(typeof(Projectile_Explosive),"Impact").Invoke(f.Bullet,new object?[]{null,true});f.Hit(lastExplosion!);Check(f.Observed,"actual blockedByShield native impact still creates damaging explosive lineage");
        f=new Fixture();f.Launch();var early=AccessTools.Method(typeof(Program),nameof(EarlyFactoryPrefix));sink.Patch(factory,prefix:new HarmonyMethod(early){priority=Priority.First});
        earlyFactory=()=>factory.Invoke(null,f.Args());f.Explode();Check(createdExplosions.Count==2,"real earlier prefix recursively invokes matching factory");
        foreach(var secondary in createdExplosions)f.Hit(secondary);Check(!f.Observed&&f.Loss,"foreign earlier factory prefix cannot bind nested or outer completion");sink.Unpatch(factory,early);f.Explode();f.Hit(lastExplosion!);Check(f.Observed,"later verified factory recovers after foreign prefix removal");
        foreach(var kind in new[]{"instigator","definition","args"})
        {
            f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;
            replacement=info=>new DamageInfo(kind=="definition"?Raw<DamageDef>():info.Def,info.Amount,instigator:kind=="definition"?f.Attacker:f.Victim);
            var mutation=AccessTools.Method(typeof(Program),kind=="args"?nameof(SubstituteArgs):nameof(SubstituteDamage));sink.Patch(Method("DamageTarget"),prefix:new HarmonyMethod(mutation){priority=Priority.First});
            f.Hit(blast);Check(!f.Observed&&f.Loss,"foreign "+kind+" replacement cannot certify original local DamageInfo");
            Check(kind=="definition"?!ReferenceEquals(lastDamage.Def,f.Damage):ReferenceEquals(lastDamage.Instigator,f.Victim),"actual forwarded native damage was substituted by "+kind);
            sink.Unpatch(Method("DamageTarget"),mutation);replacement=null;f.Hit(blast);Check(f.Observed,"later unambiguous damage recovers after "+kind+" replacement removal");
        }
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;var foreign=Raw<Explosion>();foreign.thingIDNumber=blast.thingIDNumber;f.Hit(foreign);Check(!f.Observed,"same explosion ID on another object cannot substitute lineage");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;Set(f.Game,"currentMapIndex",(sbyte)-1);f.Hit(blast);Check(!f.Observed,"changed exact map context refuses delayed damage");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;Set(f.Game.components[0],"LoadToken","replacement-load");f.Hit(blast);Check(!f.Observed,"changed exact load identity refuses delayed damage");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;Call(Producer,null,"ApplyExplosionDamage",f.Victim,new DamageInfo(f.Damage,3,instigator:f.Attacker),blast,Raw<DamageWorker_AddInjury>());Check(!f.Observed,"same worker class cannot replace exact damage-definition worker");
        f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;var games=Producer.GetField("Games",F)!.GetValue(null)!;object?[] lookup={f.Game,null};Call(games.GetType(),games,"TryGetValue",lookup);var state=lookup[1]!;var blasts=(System.Collections.IDictionary)Get(state,"Explosions")!;var template=blasts[blast]!;
        while(blasts.Count<16384){var placeholder=Raw<Explosion>();placeholder.thingIDNumber=10000+blasts.Count;blasts.Add(placeholder,template);}
        var later=f.NewBullet(700);f.Launch(later);f.Explode(source:later);var overflow=lastExplosion!;Check(f.Loss,"full blast lineage storage reports incomplete tracking");f.Hit(overflow);Check(!f.Observed,"overflow explosion cannot earn attribution");f.Hit(blast);Check(f.Observed,"capacity exhaustion preserves earlier independent explosion evidence");
        var registrations=new[]{new[]{"LaunchTarget","Transpilers","LaunchTranspiler"},new[]{"ImpactTarget","Transpilers","ImpactTranspiler"},new[]{"DamageTarget","Prefixes","DamagePrefix"},new[]{"DamageTarget","Finalizers","DamageFinalizer"},new[]{"ExplodeTarget","Transpilers","ExplodeTranspiler"},new[]{"DoExplosionTarget","Transpilers","StartTranspiler"},new[]{"DoExplosionTarget","Prefixes","DoExplosionPrefix"},new[]{"DoExplosionTarget","Finalizers","DoExplosionFinalizer"},new[]{"ExplosionDamageTarget","Transpilers","ExplosionDamageTranspiler"},new[]{"ExplosionCellTarget","Finalizers","CellFinalizer"}};
        foreach(var missing in registrations)
        {
            f=new Fixture();f.Launch();f.Explode();blast=lastExplosion!;new Harmony(Owner).Unpatch(Method(missing[0]),Method(missing[2]));Check(!(bool)Producer.GetProperty("ExplosiveIsReady",F)!.GetValue(null)!,"missing "+missing[2]+" fails explosion readiness");
            var calls=damageCalls;f.Hit(blast);f.Explode();Check(damageCalls==calls+1&&!f.Observed&&f.Loss,"unhealthy hooks preserve native forwarding without false evidence");
            Check((int)Producer.GetField("damageDepth",F)!.GetValue(null)! ==0&&(int)Producer.GetField("explosionDepth",F)!.GetValue(null)! ==0&&Producer.GetField("explosionTicket",F)!.GetValue(null)==null,"unhealthy native calls leave no unpaired scope");
            Call(Producer,null,"Initialize");Check((bool)Producer.GetProperty("ExplosiveIsReady",F)!.GetValue(null)!,"all hooks repaired live");
            foreach(var registration in registrations){var info=Harmony.GetPatchInfo(Method(registration[0]));var patches=(IEnumerable<Patch>)Get(info,registration[1])!;Check(patches.Count(row=>row.owner==Owner&&row.PatchMethod==Method(registration[2]))==1,"exactly one required registration after repair "+registration[2]);}
            later=f.NewBullet(800);f.Launch(later);f.Explode(source:later);f.Hit(lastExplosion!);Check(f.Observed,"later independent explosion evidence survives partial hook repair");
        }
        foreach(var pair in new[]{new[]{"RewriteExplode","ExplodeTarget","DoExplosionTarget","ExplodeWrapper"},new[]{"RewriteExplosionStart","DoExplosionTarget","StartExplosionTarget","StartWrapper"},new[]{"RewriteExplosionDamage","ExplosionDamageTarget","DamageTarget","ExplosionDamageWrapper"}})
        {
            var original=PatchProcessor.GetOriginalInstructions(Method(pair[1]));var output=Rewrite(pair[0],original);Check(Calls(output,Method(pair[3]))==1&&Calls(output,Method(pair[2]))==0,"exact native explosion callsite replaced once");
            var changed=original.Select(row=>new CodeInstruction(row)).ToList();changed.RemoveAt(changed.FindIndex(row=>Equals(row.operand,Method(pair[2]))));output=Rewrite(pair[0],changed);Check(Calls(output,Method(pair[3]))==0&&!(bool)Producer.GetProperty("ExplosiveIsReady",F)!.GetValue(null)!,"missing explosion call refuses partial rewrite");Rewrite(pair[0],original);
            changed=original.Select(row=>new CodeInstruction(row)).ToList();changed.Add(new CodeInstruction(OpCodes.Call,Method(pair[2])));output=Rewrite(pair[0],changed);Check(Calls(output,Method(pair[3]))==0,"extra explosion call refuses partial rewrite");Rewrite(pair[0],original);
            changed=original.Select(row=>new CodeInstruction(row)).ToList();var call=changed.First(row=>Equals(row.operand,Method(pair[2])));call.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));output=Rewrite(pair[0],changed);Check(Calls(output,Method(pair[3]))==0,"exception-boundary explosion call fails closed");Rewrite(pair[0],original);
            changed=original.Select(row=>new CodeInstruction(row)).ToList();call=changed.First(row=>Equals(row.operand,Method(pair[2])));var labelGenerator=new DynamicMethod("LabelFixture",typeof(void),Type.EmptyTypes).GetILGenerator();Label label;do{label=labelGenerator.DefineLabel();}while(changed.Any(row=>row.labels.Contains(label)));call.labels.Add(label);output=Rewrite(pair[0],changed);Check(output.Count(row=>row.labels.Contains(label))==1,"explosion rewrite preserves branch label once");
        }
        Check((bool)Producer.GetProperty("ExplosiveIsReady",F)!.GetValue(null)!,"final explosion IL and hook health valid");
        Console.WriteLine(checks+" actual-assembly explosion checks passed; synthetic forwarding sinks, not gameplay damage acceptance.");
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
