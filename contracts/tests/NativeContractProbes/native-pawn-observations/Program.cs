#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

internal static class NativePawnObservationsProbe
{
    private static Assembly bridge=null!;
    private static Type tools=null!;
    private static int checks;
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static void Check(bool ok,string why) {if(!ok) throw new InvalidOperationException(why);checks++;}
    private static object Wire(string name,string json) {
        var type=typeof(RimGovernor.Contracts.Wire.Common.Ref).Assembly.GetType("RimGovernor.Contracts.Wire.Observations."+name,true)!;
        return typeof(RimGovernor.Contracts.Wire.WireJson).GetMethod("Deserialize",new[]{typeof(string)})!.MakeGenericMethod(type).Invoke(null,new object[]{json})!;
    }
    private static object Get(object value,string name)=>value.GetType().GetProperty(name) is { } property ? property.GetValue(value)! : value.GetType().GetField(name,Flags)!.GetValue(value)!;
    private static void Set(object value,string name,object? field)=>value.GetType().GetProperty(name)!.SetValue(value,field);
    private static bool Valid(string json)=>(bool)tools.GetMethod("Validate",Flags)!.Invoke(null,new object?[]{Wire("ListPawnsRequest",json),null})!;
    private static bool Match(object pawn,string filter)=>(bool)tools.GetMethod("Matches",Flags)!.Invoke(null,new object?[]{pawn,Wire("PawnFilter",filter)})!;
    private static bool Throws(Action action,string name) {try {action();return false;}catch(TargetInvocationException e){return e.InnerException!.GetType().Name==name;}}
    internal static int Invoke(string[] args)
    {
        var directories=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{
            var path=directories.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);
            return path==null?null:Assembly.LoadFrom(path);
        };
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools=bridge.GetType("HomeBridge.BridgeTools.NativePawnObservationTools",true)!;
        WorkSettingsProof.Run(bridge, Check);
        AcquisitionProof.Run(bridge, Check);
        ZoneProof.Run(bridge, Check);
        const string scope="\"scope\":{\"expectedIdentity\":{\"colonyId\":\"colony\",\"loadToken\":\"load\",\"mapId\":0}}";
        Func<string,string> request=fields=>"{"+scope+(fields.Length==0?"":","+fields)+"}";
        Check(Valid(request("")),"defaults accepted");
        Check(Valid(request("\"page\":{\"limit\":1},\"filter\":{\"withinColonistDistance\":0,\"colonist\":false}")),"explicit zero/false accepted");
        Check(Valid(request("\"page\":{\"limit\":256}")),"maximum bound accepted");
        // N01.03: frozen paging is no longer refused outright -- a cursor within the
        // byte bound is accepted (its actual freshness is checked at read time by the
        // shared NativeObservationSnapshot.Cursor helper, not by Validate).
        Check(Valid(request("\"page\":{\"cursor\":\"old\"}")),"a within-bound cursor is accepted");
        foreach(var invalid in new[]{"{}",request("\"page\":{\"limit\":0}"),request("\"page\":{\"limit\":257}"),request("\"page\":{\"cursor\":\""+new string('x',4097)+"\"}"),request("\"filter\":{\"ids\":[\"Pawn1\",\"Pawn1\"]}"),request("\"filter\":{\"ids\":[\"\"]}"),request("\"filter\":{\"withinColonistDistance\":-1}"),request("\"filter\":{\"withinColonistDistance\":\"NaN\"}"),request("\"filter\":{\"withinColonistDistance\":\"Infinity\"}"),request("\"filter\":{\"nameContains\":\""+new string('x',257)+"\"}")})
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
        Set(pawn,"Dead",false);Set(pawn,"NearestColonistDistance",null);
        Check(!Match(pawn,"{\"withinColonistDistance\":0}"),"missing nearest colonist is not zero");
        Set(pawn,"NearestColonistDistance",0d);Check(Match(pawn,"{\"withinColonistDistance\":0}"),"observed zero distance included");
        var defaults=bridge.GetType("HomeBridge.BridgeTools.NativePawnDetails",true)!.GetMethod("Defaults",Flags)!;
        var detail=defaults.Invoke(null,new object?[]{null})!;
        foreach(var name in new[]{"Needs","Health","Equipment","Biography","Settings","Social","Animals"})
            Check(Get(detail,name) is true,"useful default detail "+name);
        foreach(var name in new[]{"Work","Schedule","Tend"}) Check(Get(detail,name) is false,"opt-in detail off by default "+name);
        var optOut=Wire("PawnDetails","{\"needs\":false,\"health\":false,\"equipment\":false,\"biography\":false,\"settings\":false,\"social\":false,\"animals\":false}");
        detail=defaults.Invoke(null,new[]{optOut})!;
        foreach(var name in new[]{"Needs","Health","Equipment","Biography","Settings","Social","Animals"}) Check(Get(detail,name) is false,"explicit detail opt-out "+name);
        var detailsType=bridge.GetType("HomeBridge.BridgeTools.NativePawnDetails",true)!;
        var definition=detailsType.GetMethod("DefinitionLabel",Flags)!;
        var absentLabel=definition.Invoke(null,new object?[]{"Backstory1",null})!;
        Check((string)Get(absentLabel,"DefName")=="Backstory1"&&Get(absentLabel,"Label")==null,"missing native definition label preserves exact ID and absence");
        var title=definition.Invoke(null,new object?[]{"Backstory1","Nurse"})!;
        Check((string?)Get(title,"Label")=="Nurse","actual backstory title is retained");
        var core=Wire("PawnState","{}");
        core=detailsType.GetMethod("Apply",Flags)!.Invoke(null,new object?[]{null,core,optOut,null})!;
        foreach(var block in new[]{"Needs","Health","Equipment","Biography","Settings","Social","AnimalState"})
            Check(core.GetType().GetProperty(block)!.GetValue(core)==null,"explicit opt-out leaves the block absent without native reads "+block);
        var native=Assembly.Load("Assembly-CSharp");
        var backstory=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(native.GetType("RimWorld.BackstoryDef",true)!);
        backstory.GetType().GetField("defName")!.SetValue(backstory,"BackstoryWithoutGenericLabel");
        var nativeDefinition=detailsType.GetMethod("Definition",Flags)!.Invoke(null,new[]{backstory})!;
        Check((string)Get(nativeDefinition,"DefName")=="BackstoryWithoutGenericLabel"&&Get(nativeDefinition,"Label")==null,"real native BackstoryDef with null generic label does not crash projection");
        var emptyPawn=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(native.GetType("Verse.Pawn",true)!);
        var missingEquipment=detailsType.GetMethod("Equipment",Flags)!.Invoke(null,new[]{emptyPawn})!;
        Check(!(bool)Get(missingEquipment,"Armed")!&&Get(missingEquipment,"PrimaryId")==null,"a pawn without an equipment tracker carries no weapon");
        Check(((IEnumerable)Get(missingEquipment,"Equipped")).Cast<object>().Count()==0&&((IEnumerable)Get(missingEquipment,"Apparel")).Cast<object>().Count()==0,"a pawn without gear trackers has no gear rows");
        var missingSettings=Wire("PawnSettings","{}");
        foreach(var projection in new[]{"CarePolicy","Work","AllowedArea","Schedule"})
            missingSettings=detailsType.GetMethod(projection,Flags)!.Invoke(null,new[]{emptyPawn,missingSettings})!;
        Check(Get(missingSettings,"SelfTend")==null&&Get(missingSettings,"MedicalCare")==null,"a pawn without player settings has no care policy");
        Check(Get(missingSettings,"WorkApplies") is false&&((IEnumerable)Get(missingSettings,"Work")).Cast<object>().Count()==0,"uninitialized work tracker is not queried");

        // ---- N01.03: Social fixture (present-but-empty thought trackers) ----
        // Thought/Thought_Memory/Thought_Situational cannot be constructed in this offline harness:
        // touching any field declared on RimWorld.Thought forces its static cctor, which reaches
        // ContentFinder<Texture2D>.Get -> Verse.UnityData -> a Unity Application/StackTraceUtility
        // ECall that throws SecurityException outside the real game process. So this fixture proves
        // the "trackers present but empty" path (correct empty collections and the cache-stale
        // wiring), rather than populated thought rows.
        Type[] allNativeTypes;
        try { allNativeTypes=native.GetTypes(); }
        catch(ReflectionTypeLoadException rtle) { allNativeTypes=rtle.Types.Where(t=>t!=null).ToArray()!; }
        var pawnKindDefT=allNativeTypes.First(t=>t.Name=="PawnKindDef");
        var socialKindDef=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(pawnKindDefT);
        pawnKindDefT.GetField("defName")!.SetValue(socialKindDef,"Colonist");
        pawnKindDefT.GetField("label")!.SetValue(socialKindDef,"colonist");
        var thingDefT=native.GetType("Verse.ThingDef",true)!;
        var socialPawnDef=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(thingDefT);
        thingDefT.GetField("defName")!.SetValue(socialPawnDef,"Human");
        var socialPawnType=native.GetType("Verse.Pawn",true)!;
        var socialThingType=native.GetType("Verse.Thing",true)!;
        var socialThingIdField=socialThingType.GetField("thingIDNumber",Flags)!;
        var socialGenderEnum=native.GetType("Verse.Gender",true)!;
        var socialNameSingleT=allNativeTypes.First(t=>t.Name=="NameSingle");
        object MakeSocialPawn(int id,string name) {
            var p=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(socialPawnType);
            socialThingType.GetField("def",Flags)!.SetValue(p,socialPawnDef);
            socialThingIdField.SetValue(p,id);
            socialPawnType.GetField("kindDef",Flags)!.SetValue(p,socialKindDef);
            socialPawnType.GetField("gender",Flags)!.SetValue(p,Enum.Parse(socialGenderEnum,"Male"));
            socialPawnType.GetField("nameInt",Flags)!.SetValue(p,Activator.CreateInstance(socialNameSingleT,new object?[]{name,false}));
            var mindStateT=allNativeTypes.First(t=>t.Name=="Pawn_MindState");
            var mindState=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(mindStateT);
            mindStateT.GetField("pawn",Flags)?.SetValue(mindState,p);
            var mentalHandlerT=allNativeTypes.First(t=>t.Name=="MentalStateHandler");
            var mentalHandler=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(mentalHandlerT);
            mentalHandlerT.GetField("pawn",Flags)?.SetValue(mentalHandler,p);
            mindStateT.GetField("mentalStateHandler",Flags)?.SetValue(mindState,mentalHandler);
            socialPawnType.GetField("mindState",Flags)!.SetValue(p,mindState);
            return p;
        }
        var ada=MakeSocialPawn(101,"Ada");

        var needsTrackerT=native.GetType("RimWorld.Pawn_NeedsTracker",true)!;
        var moodT=native.GetType("RimWorld.Need_Mood",true)!;
        var thoughtHandlerT=native.GetType("RimWorld.ThoughtHandler",true)!;
        var memHandlerT=native.GetType("RimWorld.MemoryThoughtHandler",true)!;
        var memType=native.GetType("RimWorld.Thought_Memory",true)!;
        var sitHandlerT=native.GetType("RimWorld.SituationalThoughtHandler",true)!;
        var sitT=native.GetType("RimWorld.Thought_Situational",true)!;
        var needsTrackerInst=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(needsTrackerT);
        var moodInst=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(moodT);
        var thoughtHandlerInst=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(thoughtHandlerT);
        var memHandlerInst=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(memHandlerT);
        var memoriesList=Activator.CreateInstance(typeof(List<>).MakeGenericType(memType))!;
        memHandlerT.GetField("memories",Flags)!.SetValue(memHandlerInst,memoriesList);
        var sitHandlerInst=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(sitHandlerT);
        var cachedThoughtsList=Activator.CreateInstance(typeof(List<>).MakeGenericType(sitT))!;
        sitHandlerT.GetField("cachedThoughts",Flags)!.SetValue(sitHandlerInst,cachedThoughtsList);
        var cachedSocialThoughts=Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(socialPawnType,sitHandlerT.GetNestedType("CachedSocialThoughts",Flags)!))!;
        sitHandlerT.GetField("cachedSocialThoughts",Flags)!.SetValue(sitHandlerInst,cachedSocialThoughts);
        sitHandlerT.GetField("thoughtsDirty",Flags)!.SetValue(sitHandlerInst,true);
        thoughtHandlerT.GetField("memories",Flags)!.SetValue(thoughtHandlerInst,memHandlerInst);
        thoughtHandlerT.GetField("situational",Flags)!.SetValue(thoughtHandlerInst,sitHandlerInst);
        moodT.GetField("thoughts",Flags)!.SetValue(moodInst,thoughtHandlerInst);
        needsTrackerT.GetField("mood",Flags)!.SetValue(needsTrackerInst,moodInst);
        socialPawnType.GetField("needs",Flags)!.SetValue(ada,needsTrackerInst);

        var socialMethod=detailsType.GetMethod("Social",Flags)!;

        var socialResultDirty=socialMethod.Invoke(null,new object?[]{ada})!;
        Check((bool)Get(socialResultDirty,"SituationalCacheStale"),"stale situational cache reflected when thoughtsDirty is true");

        sitHandlerT.GetField("thoughtsDirty",Flags)!.SetValue(sitHandlerInst,false);
        var socialResultFresh=socialMethod.Invoke(null,new object?[]{ada})!;
        Check(!(bool)Get(socialResultFresh,"SituationalCacheStale"),"fresh situational cache reflected when thoughtsDirty is false");

        Check(((IEnumerable)Get(socialResultFresh,"Memories")).Cast<object>().Count()==0,"present-but-empty memories yields zero rows");
        Check(((IEnumerable)Get(socialResultFresh,"Situational")).Cast<object>().Count()==0,"present-but-empty situational cache yields zero rows");

        // Regression guard: Social() must never mutate the thought fixtures (never
        // recalculate situational thoughts, which deletes/recreates memories as a side
        // effect in real RimWorld -- see PawnSettingsRead's class remarks).
        Check(ReferenceEquals(memHandlerT.GetField("memories",Flags)!.GetValue(memHandlerInst),memoriesList)&&memoriesList is IList{Count:0},"memories list reference and emptiness unchanged after Social()");
        Check(ReferenceEquals(sitHandlerT.GetField("cachedThoughts",Flags)!.GetValue(sitHandlerInst),cachedThoughtsList)&&cachedThoughtsList is IList{Count:0},"cached situational thoughts reference and emptiness unchanged after Social()");

        // ---- N01.03: corpse detail fixture (Apply() on a dead pawn) ----
        // Health()'s and Biography()'s populate branches are unreachable in this offline
        // harness for ANY pawn (dead or alive): HealthAIUtility.ShouldSeekMedicalRest and
        // Pawn.CombinedDisabledWorkTags both unconditionally touch Verse.ModsConfig's static
        // cctor -> GenFilePaths.SaveDataFolderPath -> a Unity Application.persistentDataPath
        // ECall that throws SecurityException outside the real game process. So this fixture
        // disables health/biography/social/animals explicitly and exercises the
        // needs/equipment/settings projection for a corpse instead.
        var corpse=MakeSocialPawn(103,"Corpse");
        var healthTrackerT=native.GetType("Verse.Pawn_HealthTracker",true)!;
        var healthTrackerInst=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(healthTrackerT);
        healthTrackerT.GetField("pawn",Flags)!.SetValue(healthTrackerInst,corpse);
        var pawnHealthStateT=native.GetType("Verse.PawnHealthState",true)!;
        healthTrackerT.GetField("healthState",Flags)!.SetValue(healthTrackerInst,Enum.Parse(pawnHealthStateT,"Dead"));
        var hediffSetT=native.GetType("Verse.HediffSet",true)!;
        var hsInst=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(hediffSetT);
        hediffSetT.GetField("pawn",Flags)!.SetValue(hsInst,corpse);
        hediffSetT.GetField("hediffs",Flags)!.SetValue(hsInst,Activator.CreateInstance(typeof(List<>).MakeGenericType(native.GetType("Verse.Hediff",true)!)));
        healthTrackerT.GetField("hediffSet",Flags)!.SetValue(healthTrackerInst,hsInst);
        var billStackT=native.GetType("RimWorld.BillStack",true)!;
        var billStackInst=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(billStackT);
        billStackT.GetField("bills",Flags)!.SetValue(billStackInst,Activator.CreateInstance(typeof(List<>).MakeGenericType(native.GetType("RimWorld.Bill",true)!)));
        healthTrackerT.GetField("surgeryBills",Flags)!.SetValue(healthTrackerInst,billStackInst);
        socialPawnType.GetField("health",Flags)!.SetValue(corpse,healthTrackerInst);
        var corpseRow=Wire("PawnState","{}");
        var corpseDetail=Wire("PawnDetails","{\"needs\":false,\"health\":false,\"equipment\":true,\"biography\":false,\"settings\":true,\"social\":false,\"animals\":false}");
        corpseRow=detailsType.GetMethod("Apply",Flags)!.Invoke(null,new object?[]{corpse,corpseRow,corpseDetail,null})!;
        Check(Get(corpseRow,"Equipment")!=null,"corpse equipment projection still populates for a dead pawn");
        Check(Get(corpseRow,"Settings")!=null,"corpse settings projection still populates for a dead pawn");
        Check(Get(corpseRow,"Needs")==null&&Get(corpseRow,"Health")==null&&Get(corpseRow,"Biography")==null,"corpse needs/health/biography stay disabled (Blocker: ModsConfig static cctor)");

        var number=tools.GetMethod("Number",Flags)!;
        Check((double)number.Invoke(null,new object[]{0d})! == 0,"observed zero retained");
        Check(Throws(()=>number.Invoke(null,new object[]{double.NaN}),"InvalidOperationException"),"nonfinite native fact fails");
        Check(Throws(()=>tools.GetMethod("Text",Flags)!.Invoke(null,new object[]{new string('x',4097)}),"ReadLimit"),"native labels are never silently truncated before matching");
        var complete=tools.GetMethod("Complete",Flags)!.Invoke(null,new object[]{0,7})!;
        Check((ulong)Get(complete,"Filtered")==7,"known empty exact-query completeness");
        Console.WriteLine(checks+" compiled pawn boundary/filter/default assertions passed; no gameplay assertions.");return 0;
    }
}
