#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
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
    private static object Call(object value,string method,params object?[] args) => value.GetType().GetMethod(method,Flags)!.Invoke(value,args)!;
    private static void Check(bool value,string label) { if (!value) throw new Exception(label); count++; }
    private static object Copy(object value) => typeof(object).GetMethod("MemberwiseClone",Flags)!.Invoke(value,null)!;
    // Authority.Owner was removed by #52: claims and releases are keyed by snapshot token and claim ID
    // alone, and authority attribution is generation continuity inside NativeControlAuthority.Owned().
    private static object Facts(bool drafted=false,ulong revision=0)
    {
        var facts=New("NativePawnFacts");
        Set(facts,"Identity",identity); Set(facts,"Pawn",pawn); Set(facts,"PawnId","Thing_Human42"); Set(facts,"Drafter",drafter);
        Set(facts,"Drafted",drafted); Set(facts,"DraftRevision",revision); Set(facts,"Spawned",true); Set(facts,"PlayerControlled",true);
        Set(facts,"Job",New("NativePawnJobFacts",new object?[]{null}));
        return facts;
    }
    private static object Observe(object record,object facts)=>Call(record,"Observe",facts);
    private static object Claim(object record,object before,object after)
    {
        var args=new object?[]{before,Get(before,"Token"),null};
        Check(Call(record,"PrepareClaim",args).ToString()=="Ready","claim admitted");
        var ticket=args[2]!; args=new object?[]{ticket,after,null};
        Check(Call(record,"CompleteClaim",args).ToString()=="Ready","false to true claim verified");
        return args[2]!;
    }
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
        var record=New("NativePawnControlRecord"); var facts=Facts(); var before=Observe(record,facts);
        Check(Get(before,"Token")!.ToString()!.Length==32,"opaque token generated");
        Check(Get(Observe(record,Copy(facts)),"Token")!.Equals(Get(before,"Token")),"unchanged facts stable token");
        var changed=Copy(facts);Set(changed,"Downed",true);
        Check(!Get(Observe(record,changed),"Token")!.Equals(Get(before,"Token")),"eligibility changes token");
        record=New("NativePawnControlRecord"); before=Observe(record,Facts(true,1));
        var inputs=new object?[]{before,Get(before,"Token"),null};
        Check(Call(record,"PrepareClaim",inputs).ToString()=="Ineligible","cannot adopt player drafted unclaimed pawn");
        record=New("NativePawnControlRecord"); before=Observe(record,Facts());
        inputs=new object?[]{before,"caller-hash",null};Check(Call(record,"PrepareClaim",inputs).ToString()=="StaleSnapshot","caller hashes refused");
        var owned=Claim(record,before,Facts(true,1)); var claim=Get(owned,"Claim")!;
        Check(Get(claim,"ClaimId")!=null,"canonical claim exposed");
        Check(claim.GetType().GetProperty("Owner",Flags)==null,"claim carries no owner token after #52");
        Check(Get(Get(Observe(record,Facts(true,1)),"Claim")!,"ClaimId")!.Equals(Get(claim,"ClaimId")),"re-observation preserves the canonical claim ID");
        var progress=Facts(true,1);Set(progress,"Job",New("NativePawnJobFacts",FormatterServices.GetUninitializedObject(game.GetType("Verse.AI.Job",true)!)));
        Check(Get(Observe(record,progress),"Claim")!=null,"ordinary simulation job changes preserve claim");
        Call(record,"Ordered",true); var ordered=Facts(true,1);Set(ordered,"OrderRevision",1UL);
        Check(Get(Observe(record,ordered),"Claim")!=null,"causally owned admitted order preserves claim");
        Call(record,"Ordered",false);Set(ordered,"OrderRevision",2UL);
        Check(Get(Observe(record,ordered),"Claim")==null,"external successful order invalidates claim");
        record=New("NativePawnControlRecord");before=Observe(record,Facts());owned=Claim(record,before,Facts(true,1));
        Check(Get(Observe(record,Facts(true,3)),"Claim")==null,"undraft redraft between reads invalidates claim");
        record=New("NativePawnControlRecord");before=Observe(record,Facts());owned=Claim(record,before,Facts(true,1));claim=Get(owned,"Claim")!;
        inputs=new object?[]{owned,Get(owned,"Token"),"other-claim",null};
        Check(Call(record,"PrepareRelease",inputs).ToString()=="ClaimMismatch","cleanup claim ID exact");
        inputs=new object?[]{owned,Get(owned,"Token"),Get(claim,"ClaimId"),null};
        Check(Call(record,"PrepareRelease",inputs).ToString()=="Ready","cleanup admitted without authority or global generation");
        var release=inputs[3]!;
        Check(Call(record,"PrepareRelease",inputs).ToString()=="Uncertain","uncompleted cleanup remains correlated uncertain");
        var complete=new object?[]{release,Facts(false,2),null};
        Check(Call(record,"CompleteRelease",complete).ToString()=="Ready","cleanup success requires exact undrafted readback");
        var after=complete[2]!; inputs[0]=after;
        Check(Call(record,"PrepareRelease",inputs).ToString()=="AlreadyReleased","exact replay precedes old snapshot token CAS");
        inputs[1]=Get(after,"Token");Check(Call(record,"PrepareRelease",inputs).ToString()=="ClaimMismatch","changed replay request not accepted");inputs[1]=Get(owned,"Token");
        var stale=Facts(false,2);Set(stale,"OrderRevision",1UL);inputs[0]=Observe(record,stale);
        Check(Call(record,"PrepareRelease",inputs).ToString()=="StaleSnapshot","changed order refuses old release replay");
        var context=Facts(false,2);Set(context,"ContextRevision",1UL);inputs[0]=Observe(record,context);
        Check(Call(record,"PrepareRelease",inputs).ToString()=="StaleIdentity","away and back context cannot revive cleanup");
        record=New("NativePawnControlRecord");before=Observe(record,Facts());owned=Claim(record,before,Facts(true,1));claim=Get(owned,"Claim")!;
        inputs=new object?[]{owned,Get(owned,"Token"),Get(claim,"ClaimId"),null};Call(record,"PrepareRelease",inputs);release=inputs[3]!;
        complete=new object?[]{release,Facts(true,1),null};Check(Call(record,"CompleteRelease",complete).ToString()=="Uncertain","receipt without undraft is not completion");
        complete=new object?[]{release,Facts(false,4),null};Check(Call(record,"CompleteRelease",complete).ToString()=="Uncertain","multiple later setters cannot establish release");
        long now=0;
        var authorityType=runtime.GetType("HomeBridge.BridgeTools.NativeControlAuthority",true)!;
        var contextDelegate=Expression.Lambda(typeof(Func<>).MakeGenericType(identity.GetType()),Expression.Constant(identity,identity.GetType())).Compile();
        var authority=Activator.CreateInstance(authorityType,Flags,null,new object[]{nativeGame,contextDelegate,new Func<long>(()=>now),1UL},null)!;
        var states=authorityType.GetField("States",Flags)!.GetValue(null)!;
        var getValue=states.GetType().GetMethod("GetValue")!;
        var callbackType=getValue.GetParameters()[1].ParameterType;
        var parameter=Expression.Parameter(nativeGame.GetType());
        var factory=Expression.Lambda(callbackType,Expression.Constant(authority,authorityType),parameter).Compile();
        getValue.Invoke(states,new[]{nativeGame,factory});
        Call(authority,"SetHookHealth",true);
        var modeType=runtime.GetType("HomeBridge.BridgeTools.NativeControlMode",true)!;
        var revokeReason=runtime.GetType("HomeBridge.BridgeTools.NativeControlRevocationReason",true)!;
        // Mode+generation replaced the owner lease (#52): Auto activates, every transition advances the
        // generation, and there is no expiry clock to run out.
        Check((bool)Get(Call(authority,"SetMode",1UL,Enum.Parse(modeType,"Auto")),"Success")!,"real native authority set to Auto for admission test");
        var activeMethod=Type("NativePawnControlState").GetMethod("Active",Flags)!;
        var causalMethod=Type("NativePawnControlState").GetMethod("CausallyOwned",Flags)!;
        Check(!(bool)causalMethod.Invoke(null,new[]{nativeGame})!,"no causal scope outside Owned()");
        using((IDisposable)Call(authority,"Owned"))
        {
            Check((bool)activeMethod.Invoke(null,new[]{nativeGame})!,"Auto authority is the claim admission predicate");
            record=New("NativePawnControlRecord");before=Observe(record,Facts());
            inputs=new object?[]{before,Get(before,"Token"),null};Call(record,"PrepareClaim",inputs);var claimTicket=inputs[2]!;
            now=1001;
            Check((bool)activeMethod.Invoke(null,new[]{nativeGame})!,"wall time alone never expires Auto authority");
            complete=new object?[]{claimTicket,Facts(true,1),null};
            Check(Call(record,"CompleteClaim",complete).ToString()=="Ready","admitted transition completes into a claim");
            var verifiedClaim=Get(complete[2]!,"Claim")!;
            bool causal=(bool)causalMethod.Invoke(null,new[]{nativeGame})!;
            Check(causal,"generation continuity inside Owned() attributes the claim");
            var externalOrder=Call(authority,"RevokeExternal",Enum.Parse(revokeReason,"ExternalOrder"));
            Check((bool)Get(externalOrder,"Active")! && (ulong)Get(externalOrder,"Generation")! == 2UL,"external order inside the causal owned scope does not revoke");
            Call(record,"Ordered",causal);
            var moved=Facts(true,1);Set(moved,"OrderRevision",1UL);var movedSnapshot=Observe(record,moved);
            Check(Get(Get(movedSnapshot,"Claim")!,"ClaimId")!.Equals(Get(verifiedClaim,"ClaimId")),"causally owned order preserves exact cleanup claim");
            Check((bool)Get(Call(authority,"Revoke",2UL,Enum.Parse(revokeReason,"Manual")),"Success")!,"manual revoke advances generation inside the scope");
            var revoked=Call(authority,"Status");
            Check(!(bool)Get(revoked,"Active")! && Get(revoked,"Reason")!.ToString()=="Manual" && (ulong)Get(revoked,"Generation")! == 3UL,"revocation retains reason and new generation");
            Check(!(bool)activeMethod.Invoke(null,new[]{nativeGame})!,"revoked authority refuses new claim admission predicate");
            Check(!(bool)causalMethod.Invoke(null,new[]{nativeGame})!,"generation change ends causal attribution for later orders");
            inputs=new object?[]{movedSnapshot,Get(movedSnapshot,"Token"),Get(verifiedClaim,"ClaimId"),null};
            Check(Call(record,"PrepareRelease",inputs).ToString()=="Ready","claim remains exactly releasable without authority");
            Call(record,"Ordered",(bool)causalMethod.Invoke(null,new[]{nativeGame})!);
            var later=Facts(true,1);Set(later,"OrderRevision",2UL);
            Check(Get(Observe(record,later),"Claim")==null,"order after revocation is external and drops the claim");
        }
        record=New("NativePawnControlRecord");var animal=Facts();Set(animal,"Drafter",null!);Set(animal,"PlayerControlled",false);
        var animalSnapshot=Observe(record,animal);
        Check(Get(animalSnapshot,"Token")!=null && Get(animalSnapshot,"Claim")==null,"no-drafter target gets same opaque unowned snapshot");
        Check(!(bool)Get(animalSnapshot,"Eligible")!,"no-drafter animal never eligible to draft");
        Check(Get(Observe(record,Copy(animal)),"Token")!.Equals(Get(animalSnapshot,"Token")),"unchanged no-drafter target token stable");
        var controlledWithoutDrafter=Copy(animal);Set(controlledWithoutDrafter,"PlayerControlled",true);
        Check(!(bool)Get(Observe(record,controlledWithoutDrafter),"Eligible")!,"player-controlled flag cannot synthesize a draft controller");
        inputs=new object?[]{animalSnapshot,Get(animalSnapshot,"Token"),null};
        Check(Call(record,"PrepareClaim",inputs).ToString()=="Ineligible","no synthetic animal draft claim");
        animalSnapshot=Observe(record,animal);
        var replaced=Copy(animal);Set(replaced,"Drafter",drafter);
        Check(!Get(Observe(record,replaced),"Token")!.Equals(Get(animalSnapshot,"Token")),"actual drafter presence changes target token");
        animalSnapshot=Observe(record,animal);
        var downed=Copy(animal);Set(downed,"Downed",true);
        Check(!Get(Observe(record,downed),"Token")!.Equals(Get(animalSnapshot,"Token")),"target downed change invalidates CAS");
        animalSnapshot=Observe(record,animal);
        var changedOrder=Copy(animal);Set(changedOrder,"OrderRevision",1UL);
        Check(!Get(Observe(record,changedOrder),"Token")!.Equals(Get(animalSnapshot,"Token")),"target native order epoch invalidates CAS");
        Console.WriteLine(count+" compiled pawn state assertions passed; no game or hook installation performed.");return 0;
    }
}
