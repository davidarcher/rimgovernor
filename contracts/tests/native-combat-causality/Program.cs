#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

internal static class Program
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly bridge=null!, game=null!;
    static int checks;
    static Type Native(string n)=>game.GetType(n,true)!;
    static Type Hook=>bridge.GetType("HomeBridge.BridgeTools.NativeCombatCausality",true)!;
    static Type RecordType=>bridge.GetType("HomeBridge.BridgeTools.NativeCombatDamageRecord",true)!;
    static object Raw(Type t)=>FormatterServices.GetUninitializedObject(t);
    static object Raw(string n)=>Raw(Native(n));
    static FieldInfo Field(Type t,string n) { for(Type? p=t;p!=null;p=p.BaseType) { var f=p.GetField(n,F|BindingFlags.DeclaredOnly);if(f!=null)return f; } throw new MissingFieldException(t.FullName,n); }
    static void Set(object o,string n,object? v)=>Field(o.GetType(),n).SetValue(o,v);
    static void Static(string t,string n,object? v)=>Field(Native(t),n).SetValue(null,v);
    static object? Get(object o,string n)=>o.GetType().GetProperty(n,F)?.GetValue(o)??Field(o.GetType(),n).GetValue(o);
    static object? Call(Type t,object? o,string n,params object?[] a)=>t.GetMethod(n,F)!.Invoke(o,a);
    static void Check(bool b,string why){if(!b)throw new Exception(why);checks++;}
    static bool Flag(object o,string n)=>(bool)Get(o,n)!;
    static Fixture? activeFixture;
    sealed class Fixture : IDisposable
    {
        internal object Game=Raw("Verse.Game"), Map=Raw("Verse.Map"), Attacker=Raw("Verse.Pawn"), Target=Raw("Verse.Pawn"), Job=Raw("Verse.AI.Job");
        internal object Record=null!;
        internal object? Scope;
        internal bool Allowed=true;
        internal Fixture(bool scope=true)
        {
        activeFixture?.Dispose();activeFixture=this;
            var maps=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(Native("Verse.Map")))!;maps.Add(Map);
            Set(Game,"maps",maps);Set(Game,"currentMapIndex",(sbyte)0);var tm=Raw("Verse.TickManager");Set(tm,"ticksGameInt",123);Set(Game,"tickManager",tm);
            Static("Verse.Current","gameInt",Game);
            var pawnDef=Raw("Verse.ThingDef");Set(pawnDef,"defName","Human");int id=1;
            foreach(var p in new[]{Attacker,Target}) {Set(p,"def",pawnDef);Set(p,"thingIDNumber",id++);Set(p,"mapIndexOrState",(sbyte)0);var health=Raw("Verse.Pawn_HealthTracker");Set(health,"healthState",Enum.Parse(Native("Verse.PawnHealthState"),"Mobile"));Set(p,"health",health);}
            var def=Raw("Verse.JobDef");Static("RimWorld.JobDefOf","AttackMelee",def);Set(Job,"def",def);Set(Job,"loadID",41);
            var targetInfo=Activator.CreateInstance(Native("Verse.LocalTargetInfo"),Target)!;Set(Job,"targetA",targetInfo);
            var jobs=Raw("Verse.AI.Pawn_JobTracker");Set(jobs,"curJob",Job);Set(Attacker,"jobs",jobs);
            Record=Call(Hook,null,"Track",Game,Attacker,Target,Job,new Func<bool>(()=>Allowed))!;
            if(scope)Scope=Open();
        }
        internal object Info(object? instigator=null){var d=Activator.CreateInstance(Native("Verse.DamageInfo"))!;Set(d,"instigatorInt",instigator??Attacker);return d;}
        internal object? Before(object? info=null,object? victim=null){object?[] a={victim??Target,info??Info(),null};Call(Hook,null,"BeforeDamage",a);return a[2];}
        internal object? Open(object? caster=null,object? target=null)
        {
            var verb=Raw("RimWorld.Verb_MeleeAttackDamage");Set(verb,"caster",caster??Attacker);
            object?[] a={verb,Activator.CreateInstance(Native("Verse.LocalTargetInfo"),target??Target),null};
            Call(Hook,null,"BeforeMelee",a);return a[2];
        }
        internal object? Close(object? scope,Exception? error=null)=>Call(Hook,null,"EndMelee",error,scope);
        internal object? End(object? frame,Exception? error=null)=>Call(Hook,null,"EndDamage",error,frame);
        internal void After(object? pending,float amount=1,object? info=null)
        {
            try { var result=Raw("Verse.DamageWorker+DamageResult");Set(result,"totalDamageDealt",amount);Call(Hook,null,"AfterDamage",info??Info(),result,pending); }
            finally { End(pending); }
        }
        internal void Refused(string label,object? info=null,object? victim=null) {After(Before(info,victim),info:info);Check(!Flag(Record,"ObservedDamage"),label);}
        public void Dispose(){if(Scope!=null){Close(Scope);Scope=null;}}

        internal void Health(string state)=>Set(Get(Target,"health")!,"healthState",Enum.Parse(Native("Verse.PawnHealthState"),state));
    }
    static object Record()=>Raw(RecordType);
    static void Result(object r,float damage,bool beforeDead=false,bool beforeDown=false,bool dead=false,bool down=false,long tick=1)=>Call(RecordType,r,"Record",damage,beforeDead,beforeDown,dead,down,tick);
    static int Main(string[] args) { try { Run(args); return 0; } catch(Exception e) { Console.Error.WriteLine(e); return 1; } }
    static void Run(string[] args)
    {
        var dirs=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var p=dirs.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return p==null?null:Assembly.LoadFrom(p);};
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var r in bridge.GetReferencedAssemblies())Assembly.Load(r);
        game=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp");
        foreach(var amount in new[]{0f,-1f,float.NaN,float.PositiveInfinity,float.NegativeInfinity}){var r=Record();Result(r,amount,dead:true,down:true);Check(!Flag(r,"ObservedDamage")&&!Flag(r,"CausedDeath")&&!Flag(r,"CausedDowning"),"invalid damage rejected");}
        var record=Record();Result(record,1,beforeDead:true,dead:true);Check(!Flag(record,"ObservedDamage"),"predead rejected");
        Result(record,1,tick:-1);Check(!Flag(record,"ObservedDamage"),"negative tick rejected");
        Result(record,1,beforeDown:true,down:true,tick:0);Check(Flag(record,"ObservedDamage")&&!Flag(record,"CausedDowning")&&(long)Get(record,"ObservedTick")! ==0,"prior downed positive damage known at tick zero");
        Result(record,1,down:true,tick:4);Check(Flag(record,"CausedDowning")&&!Flag(record,"CausedDeath"),"new downing attributed");
        Result(record,1,dead:true,tick:5);Check(Flag(record,"CausedDeath")&&Flag(record,"CausedDowning")&&(long)Get(record,"ObservedTick")! ==5,"transitions retained across observed hits");
        Check(!(bool)Hook.GetProperty("IsReady",F)!.GetValue(null)!,"health query does not install hooks");
        Static("RimWorld.DefOfHelper","bindingNow",true); // Supply explicit test definitions without engine startup.
        Static("Verse.UnityDataInitializer","initializing",true); // Test process has no Unity engine; avoid its startup warning ECall.
        Static("Verse.UnityData","mainThreadId",Thread.CurrentThread.ManagedThreadId);
        Call(Hook,null,"Initialize");Check((bool)Hook.GetProperty("IsReady",F)!.GetValue(null)!,"actual Harmony prefix and postfix installed");
        var f=new Fixture(false);f.Refused("exact melee job without concrete verb scope cannot earn credit");
        f=new Fixture();var pending=f.Before();f.Health("Down");f.After(pending);Check(Flag(f.Record,"CausedDowning"),"matching concrete melee scope attributes new downing");
        f=new Fixture();Check((int)Get(f.Record,"JobId")! ==41,"immutable job load identity captured");Set(f.Job,"loadID",42);f.Refused("pooled job reuse rejected within captured scope");
        f=new Fixture(false);Set(f.Job,"loadID",42);f.Scope=f.Open();f.Refused("pooled job reuse rejected before scope");
        f=new Fixture();Set(f.Job,"def",Raw("Verse.JobDef"));f.Refused("changed job definition rejected");
        f=new Fixture();var worker=new Thread(()=>f.After(f.Before()));worker.Start();worker.Join();Check(!Flag(f.Record,"ObservedDamage"),"worker thread cannot capture attribution");
        f=new Fixture();f.Refused("wrong damage instigator rejected",f.Info(Raw("Verse.Pawn")));
        Set(Get(f.Attacker,"jobs")!,"curJob",Raw("Verse.AI.Job"));f.Refused("wrong current job rejected");
        f=new Fixture();Set(f.Job,"targetA",Activator.CreateInstance(Native("Verse.LocalTargetInfo"),f.Attacker)!);f.Refused("wrong job target rejected");
        f=new Fixture();f.Allowed=false;f.Refused("ownership guard rejected");
        f=new Fixture();f.Health("Dead");f.Refused("predead target rejected");
        f=new Fixture();pending=f.Before();Static("Verse.Current","gameInt",Raw("Verse.Game"));f.After(pending);Check(!Flag(f.Record,"ObservedDamage"),"changed game after prefix rejected");
        f=new Fixture();pending=f.Before();Set(f.Game,"currentMapIndex",(sbyte)-1);f.After(pending);Check(!Flag(f.Record,"ObservedDamage"),"changed map after prefix rejected");
        f=new Fixture();pending=f.Before();var nested=f.Before();f.Health("Down");f.After(nested);Check(!Flag(f.Record,"ObservedDamage"),"nested same-instigator damage never earns credit");f.Health("Dead");f.After(pending);Check(!Flag(f.Record,"ObservedDamage"),"nested damage invalidates outer transition attribution");
        f=new Fixture();pending=f.Before();nested=f.Before(f.Info(Raw("Verse.Pawn")));f.After(nested,info:f.Info(Raw("Verse.Pawn")));f.Health("Dead");f.After(pending);Check(!Flag(f.Record,"ObservedDamage"),"nested wrong-instigator damage invalidates outer attribution");
        foreach(var amount in new[]{0f,-1f,float.NaN,float.PositiveInfinity,float.NegativeInfinity}) {f=new Fixture();pending=f.Before();f.Health("Dead");f.After(pending,amount);Check(!Flag(f.Record,"ObservedDamage")&&!Flag(f.Record,"CausedDeath"),"invalid damage total cannot prove death");}
        f=new Fixture();f.Health("Down");pending=f.Before();f.After(pending);Check(Flag(f.Record,"ObservedDamage")&&!Flag(f.Record,"CausedDowning"),"prior downing not newly attributed");
        f=new Fixture();pending=f.Before();f.After(pending,1,f.Info(Raw("Verse.Pawn")));Check(!Flag(f.Record,"ObservedDamage"),"changed result instigator rejected");
        f=new Fixture();f.Refused("untracked victim rejected",victim:f.Attacker);
        f=new Fixture();Set(f.Game,"currentMapIndex",(sbyte)-1);f.Refused("changed map before prefix rejected");
        f=new Fixture(false);f.Scope=f.Open(caster:f.Target);f.Refused("wrong concrete verb caster rejected");
        f=new Fixture(false);f.Scope=f.Open(target:f.Attacker);f.Refused("wrong concrete verb target rejected");
        f=new Fixture();var inner=f.Open(target:f.Attacker);f.Refused("nested wrong-target verb masks outer matching scope");f.Close(inner);f.After(f.Before());Check(Flag(f.Record,"ObservedDamage"),"outer matching scope restored after nested verb");
        f=new Fixture();inner=f.Open();var error=new InvalidOperationException("original native failure");Check(ReferenceEquals(f.Close(inner,error),error),"melee finalizer preserves original exception");f.After(f.Before());Check(Flag(f.Record,"ObservedDamage"),"nested exception restores outer scope");
        f=new Fixture();Check(ReferenceEquals(f.Close(f.Scope,error),error),"outer exception preserved");f.Scope=null;f.Refused("outer exception clears scope");
        f=new Fixture();pending=f.Before();Check(ReferenceEquals(f.End(pending,error),error),"damage finalizer preserves original exception");f.After(f.Before());Check(Flag(f.Record,"ObservedDamage"),"damage exception restores depth for next sequential hit");
        f=new Fixture();pending=f.Before();inner=f.Open();nested=f.Before();f.Health("Dead");f.After(nested);f.Close(inner);f.After(pending);Check(!Flag(f.Record,"ObservedDamage"),"nested melee verb during damage cannot acquire attribution");
        f=new Fixture();pending=f.Before();nested=f.Before();f.End(nested,error);f.End(pending,error);f.After(f.Before());Check(Flag(f.Record,"ObservedDamage"),"nested damage exceptions restore depth");
        var harmonyType=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="0Harmony").GetType("HarmonyLib.Harmony",true)!;
        var harmony=Activator.CreateInstance(harmonyType,"rimgovernor.combat-causality")!;
        foreach(var pair in new[]{new[]{"Target","Postfix"},new[]{"Target","Prefix"},new[]{"Target","DamageFinalizer"},new[]{"MeleeTarget","MeleePrefix"},new[]{"MeleeTarget","MeleeFinalizer"}})
        {
            f=new Fixture();pending=f.Before();
            var target=(MethodInfo)Hook.GetField(pair[0],F)!.GetValue(null)!;
            var callback=(MethodInfo)Hook.GetField(pair[1],F)!.GetValue(null)!;
            harmonyType.GetMethod("Unpatch",new[]{typeof(MethodBase),typeof(MethodInfo)})!.Invoke(harmony,new object[]{target,callback});
            Check(!(bool)Hook.GetProperty("IsReady",F)!.GetValue(null)!,"removed required "+pair[1]+" detected");
            f.After(pending);Check(!Flag(f.Record,"ObservedDamage"),"patch loss cannot produce evidence");
            f.Refused("missing required hook refuses capture");
            Call(Hook,null,"Initialize");Check((bool)Hook.GetProperty("IsReady",F)!.GetValue(null)!,"exact hook restored");
            foreach(var registration in new[]{new[]{"Target","Prefixes","Prefix"},new[]{"Target","Postfixes","Postfix"},new[]{"Target","Finalizers","DamageFinalizer"},new[]{"MeleeTarget","Prefixes","MeleePrefix"},new[]{"MeleeTarget","Finalizers","MeleeFinalizer"}})
            {
                var patched=(MethodInfo)Hook.GetField(registration[0],F)!.GetValue(null)!;
                var expected=(MethodInfo)Hook.GetField(registration[2],F)!.GetValue(null)!;
                var info=harmonyType.GetMethod("GetPatchInfo",F)!.Invoke(null,new object[]{patched})!;
                var registrations=((IEnumerable)Get(info,registration[1])!).Cast<object>().Count(row=>(string)Get(row,"owner")! =="rimgovernor.combat-causality" && Equals(Get(row,"PatchMethod"),expected));
                Check(registrations==1,"exactly one owner/method registration after repair: "+registration[2]+" count="+registrations);
            }
            f.Dispose();f.Scope=f.Open();f.After(f.Before());Check(Flag(f.Record,"ObservedDamage"),"positive concrete melee callback works after repair");
        }
        activeFixture?.Dispose();
        Check(Hook.GetField("meleeScope",F)!.GetValue(null)==null && (int)Hook.GetField("damageDepth",F)!.GetValue(null)! ==0,"all scope and damage depth cleaned up");
        Console.WriteLine(checks+" actual-assembly causality checks passed; simulated callback state, not gameplay damage acceptance.");
    }
}
