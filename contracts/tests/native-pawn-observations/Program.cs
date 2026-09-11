#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

internal static class Program
{
    private static Assembly bridge=null!;
    private static Type tools=null!;
    private static int checks;
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static void Check(bool ok,string why) {if(!ok) throw new InvalidOperationException(why);checks++;}
    private static object Wire(string name,string json) {
        var type=bridge.GetType("RimGovernor.Protocol.Observations."+name,true)!;
        var parser=type.GetProperty("Parser")!.GetValue(null)!;
        return parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!;
    }
    private static object Get(object value,string name)=>value.GetType().GetProperty(name)!.GetValue(value)!;
    private static void Set(object value,string name,object field)=>value.GetType().GetProperty(name)!.SetValue(value,field);
    private static bool Valid(string json)=>(bool)tools.GetMethod("Validate",Flags)!.Invoke(null,new object?[]{Wire("ListPawnsRequest",json),null})!;
    private static bool Match(object pawn,string filter)=>(bool)tools.GetMethod("Matches",Flags)!.Invoke(null,new object?[]{pawn,Wire("PawnFilter",filter)})!;
    private static bool Throws(Action action,string name) {try {action();return false;}catch(TargetInvocationException e){return e.InnerException!.GetType().Name==name;}}
    private static int Main(string[] args)
    {
        var directories=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{
            var path=directories.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);
            return path==null?null:Assembly.LoadFrom(path);
        };
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools=bridge.GetType("HomeBridge.BridgeTools.NativePawnObservationTools",true)!;
        const string scope="\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string,string> request=fields=>"{"+scope+(fields.Length==0?"":","+fields)+"}";
        Check(Valid(request("")),"defaults accepted");
        Check(Valid(request("\"page\":{\"limit\":1},\"filter\":{\"withinColonistDistance\":0,\"colonist\":false}")),"explicit zero/false accepted");
        Check(Valid(request("\"page\":{\"limit\":256}")),"maximum bound accepted");
        foreach(var invalid in new[]{"{}",request("\"page\":{\"limit\":0}"),request("\"page\":{\"limit\":257}"),request("\"page\":{\"cursor\":\"old\"}"),request("\"filter\":{\"ids\":[\"Pawn1\",\"Pawn1\"]}"),request("\"filter\":{\"ids\":[\"\"]}"),request("\"filter\":{\"withinColonistDistance\":-1}"),request("\"filter\":{\"withinColonistDistance\":\"NaN\"}"),request("\"filter\":{\"withinColonistDistance\":\"Infinity\"}"),request("\"filter\":{\"nameContains\":\""+new string('x',257)+"\"}")})
            Check(!Valid(invalid),"invalid query refused: "+invalid);
        var ids=string.Join(",",Enumerable.Range(0,256).Select(i=>"\"Pawn"+i+"\""));
        Check(Valid(request("\"filter\":{\"ids\":["+ids+"]}")),"256 exact IDs accepted");
        Check(!Valid(request("\"filter\":{\"ids\":["+ids+",\"extra\"]}")),"257 exact IDs refused");
        var pawn=Wire("PawnState","{\"pawn\":{\"id\":\"Human123\",\"label\":\"Ada Archer\"},\"colonist\":true,\"humanlike\":true,\"dead\":false,\"nearestColonistDistance\":3}");
        Check(Match(pawn,"{}"),"unfiltered living pawn included");
        Check(Match(pawn,"{\"ids\":[\"Human123\"],\"nameContains\":\"aDA\",\"animal\":false,\"withinColonistDistance\":3}"),"intersection and inclusive distance");
        Check(!Match(pawn,"{\"ids\":[\"123\"]}"),"numeric suffix is not exact ID");
        Check(!Match(pawn,"{\"ids\":[\"human123\"]}"),"ID case is exact");
        Check(!Match(pawn,"{\"nameContains\":\"missing\"}"),"name filter narrows");
        Check(!Match(pawn,"{\"withinColonistDistance\":2.99}"),"distance excludes outside boundary");
        foreach(var name in new[]{"Colonist","Prisoner","Animal","Humanlike","Mechanoid","Tame","Wild","Hostile","Downed","Drafted"}) {
            var json=char.ToLowerInvariant(name[0])+name.Substring(1);
            Set(pawn,name,true);Check(Match(pawn,"{\""+json+"\":true}"),"true inclusion "+name);
            Check(!Match(pawn,"{\""+json+"\":false}"),"explicit false exclusion "+name);
            Set(pawn,name,false);Check(Match(pawn,"{\""+json+"\":false}"),"false inclusion "+name);
            Check(!Match(pawn,"{\""+json+"\":true}"),"true exclusion "+name);
        }
        Set(pawn,"Dead",true);Check(!Match(pawn,"{}"),"dead excluded by default");Check(Match(pawn,"{\"includeDead\":true}"),"dead opt-in");
        Set(pawn,"Dead",false);pawn.GetType().GetMethod("ClearNearestColonistDistance")!.Invoke(pawn,null);
        Check(!Match(pawn,"{\"withinColonistDistance\":0}"),"missing nearest colonist is not zero");
        Set(pawn,"NearestColonistDistance",0d);Check(Match(pawn,"{\"withinColonistDistance\":0}"),"observed zero distance included");
        var defaults=bridge.GetType("HomeBridge.BridgeTools.NativePawnDetails",true)!.GetMethod("Defaults",Flags)!;
        var detail=defaults.Invoke(null,new object?[]{null})!;
        foreach(var name in new[]{"Needs","Health","Equipment","Biography","Settings","Social","Animals"})
            Check((bool)Get(detail,"Has"+name)&&(bool)Get(detail,name),"useful default detail "+name);
        Check((bool)Get(detail,"HasVisibleHediffsOnly")&&!(bool)Get(detail,"VisibleHediffsOnly"),"no hidden hediff filtering by default");
        detail=defaults.Invoke(null,new[]{Wire("PawnDetails","{\"needs\":false,\"health\":false,\"equipment\":false,\"biography\":false,\"settings\":false,\"social\":false,\"animals\":false,\"visibleHediffsOnly\":true}")})!;
        foreach(var name in new[]{"Needs","Health","Equipment","Biography","Settings","Social","Animals"}) Check(!(bool)Get(detail,name),"explicit detail opt-out "+name);
        Check((bool)Get(detail,"VisibleHediffsOnly"),"visible-only selection retained");
        var detailsType=bridge.GetType("HomeBridge.BridgeTools.NativePawnDetails",true)!;
        var definition=detailsType.GetMethod("DefinitionLabel",Flags)!;
        var absentLabel=definition.Invoke(null,new object?[]{"Backstory1",null})!;
        Check((string)Get(absentLabel,"DefName")=="Backstory1"&&!(bool)Get(absentLabel,"HasLabel"),"missing native definition label preserves exact ID and absence");
        var title=definition.Invoke(null,new object?[]{"Backstory1","Nurse"})!;
        Check((bool)Get(title,"HasLabel")&&(string)Get(title,"Label")=="Nurse","actual backstory title is retained");
        var core=Wire("PawnState","{\"needs\":{\"mood\":0},\"health\":{\"summaryFraction\":1}}");
        detailsType.GetMethod("Apply",Flags)!.Invoke(null,new object?[]{null,core,detail});
        Check(core.GetType().GetProperty("Needs")!.GetValue(core)==null&&core.GetType().GetProperty("Health")!.GetValue(core)==null,"explicit opt-out removes inherited status detail without native reads");
        var issues=((IEnumerable)Get(core,"Issues")).Cast<object>().ToArray();
        foreach(var field in new[]{"needs","health","equipment","biography","settings","social","animal_state"})
            Check(issues.Any(i=>(string)Get(i,"Field")==field&&Get(Get(i,"Unavailable"),"Reason").ToString()=="NotRequested"),"opt-out retains explicit issue "+field);
        var native=Assembly.Load("Assembly-CSharp");
        var backstory=FormatterServices.GetUninitializedObject(native.GetType("RimWorld.BackstoryDef",true)!);
        backstory.GetType().GetField("defName")!.SetValue(backstory,"BackstoryWithoutGenericLabel");
        var nativeDefinition=detailsType.GetMethod("Definition",Flags)!.Invoke(null,new[]{backstory})!;
        Check((string)Get(nativeDefinition,"DefName")=="BackstoryWithoutGenericLabel"&&!(bool)Get(nativeDefinition,"HasLabel"),"real native BackstoryDef with null generic label does not crash projection");
        var emptyPawn=FormatterServices.GetUninitializedObject(native.GetType("Verse.Pawn",true)!);
        var missingEquipment=detailsType.GetMethod("Equipment",Flags)!.Invoke(null,new[]{emptyPawn})!;
        Check(!(bool)Get(missingEquipment,"HasArmed"),"missing equipment tracker is not known unarmed");
        Check(!(bool)Get(missingEquipment,"HasInventoryItemCount"),"missing inventory tracker is not zero items");
        var gearIssues=((IEnumerable)Get(missingEquipment,"Issues")).Cast<object>().ToArray();
        foreach(var field in new[]{"armed","equipped","primary_id","apparel","inventory_weapons","inventory_item_count","carried_thing_id"})
            Check(gearIssues.Any(i=>(string)Get(i,"Field")==field&&Get(Get(i,"Unavailable"),"Reason").ToString()=="NativeComponentMissing"),"missing tracker issue "+field);
        var missingSettings=detailsType.GetMethod("Settings",Flags)!.Invoke(null,new[]{emptyPawn})!;
        Check(!(bool)Get(missingSettings,"HasSelfTend"),"missing settings tracker is not self-tend false");
        Check(((IEnumerable)Get(missingSettings,"Issues")).Cast<object>().Any(i=>(string)Get(i,"Field")=="work"),"uninitialized work tracker is not queried");
        var require=tools.GetMethod("Require",Flags)!;
        require.Invoke(null,new object[]{256,256});
        Check(Throws(()=>require.Invoke(null,new object[]{257,256}),"ReadLimit"),"collection overflow fails instead of sampling");
        var number=tools.GetMethod("Number",Flags)!;
        Check((double)number.Invoke(null,new object[]{0d})! == 0,"observed zero retained");
        Check(Throws(()=>number.Invoke(null,new object[]{double.NaN}),"InvalidOperationException"),"nonfinite native fact fails");
        Check(Throws(()=>tools.GetMethod("Text",Flags)!.Invoke(null,new object[]{new string('x',4097)}),"ReadLimit"),"native labels are never silently truncated before matching");
        var complete=tools.GetMethod("Complete",Flags)!.Invoke(null,new object[]{0,7})!;
        Check((ulong)Get(complete,"Returned")==0&&(ulong)Get(complete,"Matched")==0&&(ulong)Get(complete,"Filtered")==7&&(ulong)Get(complete,"Unreadable")==0,"known empty exact-query completeness");
        Check((bool)Get(Get(complete,"Page"),"Complete"),"known empty page complete");
        var huge=Wire("ListPawnsReply","{\"observed\":{\"pawns\":[{\"pawn\":{\"label\":\""+new string('x',1024*1024)+"\"}}]}}");
        Check(Throws(()=>tools.GetMethod("Encode",Flags)!.Invoke(null,new[]{huge}),"ReadLimit"),"whole reply byte overflow fails");
        var server=Assembly.LoadFrom(directories.Select(d=>Path.Combine(d,"RimBridgeServer.dll")).First(File.Exists));
        var binder=server.GetType("RimBridgeServer.AnnotatedExtensionCapabilityProvider",true)!.GetMethod("BindArguments",Flags)!;
        foreach(var value in new object?[]{"{}",new Dictionary<string,object>(),new List<object>(),null,17,true}) {
            var bound=(object[])binder.Invoke(null,new object?[]{tools.GetMethod("ListPawns"),new Dictionary<string,object?>{{"request",value}},null,CancellationToken.None})!;
            Check(ReferenceEquals(bound[2],value),"actual SDK binder preserves raw input");
        }
        Console.WriteLine(checks+" compiled pawn boundary/filter/default/binder assertions passed; no gameplay assertions.");return 0;
    }
}
