#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

internal static class NativePawnControlStateProbe
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly bridge = null!, game = null!, runtime = null!;
    private static int count;
    private static object identity = null!, pawn = null!, drafter = null!;
    private static Type Type(string name) => bridge.GetType("HomeBridge.BridgeTools." + name,true)!;
    private static object New(string name, params object?[] args) => Activator.CreateInstance(Type(name),Flags,null,args,null)!;
    private static object? Get(object value,string name) => value.GetType().GetProperty(name,Flags)?.GetValue(value) ?? value.GetType().GetField(name,Flags)?.GetValue(value);
    private static void Set(object value,string name,object data) => value.GetType().GetField(name,Flags)!.SetValue(value,data);
    private static object? Call(object value,string method,params object?[] args) => value.GetType().GetMethod(method,Flags)!.Invoke(value,args);
    private static void Check(bool value,string label) { if (!value) throw new Exception(label); count++; }
    private static object Copy(object value) => typeof(object).GetMethod("MemberwiseClone",Flags)!.Invoke(value,null)!;
    // Drafts are plan-owned: the record carries a snapshot token and
    // an order revision, and no draft claim.
    private static object Facts(bool drafted=false,ulong revision=0)
    {
        var facts=New("NativePawnFacts");
        Set(facts,"Identity",identity); Set(facts,"Pawn",pawn); Set(facts,"PawnId","Thing_Human42"); Set(facts,"Drafter",drafter);
        Set(facts,"Drafted",drafted); Set(facts,"DraftRevision",revision); Set(facts,"Spawned",true); Set(facts,"PlayerControlled",true);
        return facts;
    }
    private static object Observe(object record,object facts)=>Call(record,"Observe",facts)!;
    private static object Token(object snapshot)=>Get(snapshot,"Token")!;
    internal static int Invoke(string[] args)
    {
        var dirs=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=> { var path=dirs.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return path==null?null:Assembly.LoadFrom(path); };
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0])); foreach(var reference in bridge.GetReferencedAssemblies())Assembly.Load(reference);
        game=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp");
        runtime=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="RimGovernor.Runtime");
        var nativeGame=FormatterServices.GetUninitializedObject(game.GetType("Verse.Game",true)!);
        var map=FormatterServices.GetUninitializedObject(game.GetType("Verse.Map",true)!);
        pawn=FormatterServices.GetUninitializedObject(game.GetType("Verse.Pawn",true)!);
        drafter=FormatterServices.GetUninitializedObject(game.GetType("RimWorld.Pawn_DraftController",true)!);
        identity=Activator.CreateInstance(runtime.GetType("HomeBridge.BridgeTools.NativeControlIdentity",true)!,nativeGame,map,"colony","load")!;
        Check(!(bool)Type("NativePawnControlState").GetProperty("IsReady",Flags)!.GetValue(null)!,"read readiness does not install hooks");
        Check(!(bool)Type("DraftOwnership").GetProperty("Healthy",Flags)!.GetValue(null)!,"legacy draft hook remains uninstalled after typed readiness read");
        pawn.GetType().GetField("drafter",Flags)!.SetValue(pawn,drafter);
        var draftState=Type("DraftOwnership");
        Check((ulong)draftState.GetMethod("Revision",Flags)!.Invoke(null,new[]{pawn})! == 0,"unseen pawn revision is zero without initialization");
        draftState.GetMethod("BeforeDraft",Flags)!.Invoke(null,new[]{drafter});
        Check((ulong)draftState.GetMethod("Revision",Flags)!.Invoke(null,new[]{pawn})! == 1,"first setter callback advances revision");
        draftState.GetMethod("BeforeDraft",Flags)!.Invoke(null,new[]{drafter});
        Check((ulong)draftState.GetMethod("Revision",Flags)!.Invoke(null,new[]{pawn})! == 2,"even repeated same-value setter callback advances revision");
        Check(Type("NativePawnSnapshot").GetProperty("Claim",Flags)==null,"snapshot carries no draft claim after #939");
        var record=New("NativePawnControlRecord"); var facts=Facts(); var before=Observe(record,facts);
        Check(Token(before).ToString()!.Length==32,"opaque token generated");
        Check(Token(Observe(record,Copy(facts))).Equals(Token(before)),"unchanged facts stable token");
        var changed=Copy(facts);Set(changed,"Downed",true);
        Check(!Token(Observe(record,changed)).Equals(Token(before)),"eligibility changes token");
        record=New("NativePawnControlRecord"); before=Observe(record,Facts(true,1));
        Check((bool)Get(before,"Drafted")! && (bool)Get(before,"Eligible")!,"drafted eligible pawn observed");
        Check(!Token(Observe(record,Facts(true,3))).Equals(Token(before)),"undraft redraft between reads changes token");
        record=New("NativePawnControlRecord"); before=Observe(record,Facts(true,1));
        Call(record,"Ordered"); var ordered=Facts(true,1);Set(ordered,"OrderRevision",(ulong)Get(record,"OrderRevision")!);
        Check((ulong)Get(record,"OrderRevision")! == 1UL,"a taken order advances the order revision");
        Check(!Token(Observe(record,ordered)).Equals(Token(before)),"an order changes the token");
        var context=Facts(true,1);Set(context,"ContextRevision",1UL);Set(context,"OrderRevision",1UL);
        Check(!Token(Observe(record,context)).Equals(Token(Observe(record,ordered))),"a context change changes the token");
        record=New("NativePawnControlRecord");var animal=Facts();Set(animal,"Drafter",null!);Set(animal,"PlayerControlled",false);
        var animalSnapshot=Observe(record,animal);
        Check(Get(animalSnapshot,"Token")!=null,"no-drafter target gets an opaque snapshot");
        Check(!(bool)Get(animalSnapshot,"Eligible")!,"no-drafter animal never eligible to draft");
        Check(Token(Observe(record,Copy(animal))).Equals(Token(animalSnapshot)),"unchanged no-drafter target token stable");
        var controlledWithoutDrafter=Copy(animal);Set(controlledWithoutDrafter,"PlayerControlled",true);
        Check(!(bool)Get(Observe(record,controlledWithoutDrafter),"Eligible")!,"player-controlled flag cannot synthesize a draft controller");
        animalSnapshot=Observe(record,animal);
        var replaced=Copy(animal);Set(replaced,"Drafter",drafter);
        Check(!Token(Observe(record,replaced)).Equals(Token(animalSnapshot)),"actual drafter presence changes target token");
        animalSnapshot=Observe(record,animal);
        var downed=Copy(animal);Set(downed,"Downed",true);
        Check(!Token(Observe(record,downed)).Equals(Token(animalSnapshot)),"target downed change invalidates CAS");
        animalSnapshot=Observe(record,animal);
        var changedOrder=Copy(animal);Set(changedOrder,"OrderRevision",1UL);
        Check(!Token(Observe(record,changedOrder)).Equals(Token(animalSnapshot)),"target native order epoch invalidates CAS");
        Console.WriteLine(count+" compiled pawn state assertions passed; no game or hook installation performed.");return 0;
    }
}
