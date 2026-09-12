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
        var emptyColonists=Activator.CreateInstance(typeof(List<>).MakeGenericType(Assembly.Load("Assembly-CSharp").GetType("Verse.Pawn",true)!))!;
        detailsType.GetMethod("Apply",Flags)!.Invoke(null,new object?[]{null,emptyColonists,core,detail});
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

        // ---- N01.03: populated Social fixture (real relation + present-but-empty thought trackers) ----
        // Thought/Thought_Memory/Thought_Situational cannot be constructed in this offline harness:
        // touching any field declared on RimWorld.Thought forces its static cctor, which reaches
        // ContentFinder<Texture2D>.Get -> Verse.UnityData -> a Unity Application/StackTraceUtility
        // ECall that throws SecurityException outside the real game process. So this fixture proves
        // the "trackers present but empty" path (correct empty collections, no spurious Missing
        // issues, and the relation/cache-stale wiring), rather than populated thought rows.
        Type[] allNativeTypes;
        try { allNativeTypes=native.GetTypes(); }
        catch(ReflectionTypeLoadException rtle) { allNativeTypes=rtle.Types.Where(t=>t!=null).ToArray()!; }
        var pawnKindDefT=allNativeTypes.First(t=>t.Name=="PawnKindDef");
        var socialKindDef=FormatterServices.GetUninitializedObject(pawnKindDefT);
        pawnKindDefT.GetField("defName")!.SetValue(socialKindDef,"Colonist");
        pawnKindDefT.GetField("label")!.SetValue(socialKindDef,"colonist");
        var thingDefT=native.GetType("Verse.ThingDef",true)!;
        var socialPawnDef=FormatterServices.GetUninitializedObject(thingDefT);
        thingDefT.GetField("defName")!.SetValue(socialPawnDef,"Human");
        var socialPawnType=native.GetType("Verse.Pawn",true)!;
        var socialThingType=native.GetType("Verse.Thing",true)!;
        var socialThingIdField=socialThingType.GetField("thingIDNumber",Flags)!;
        var socialGenderEnum=native.GetType("Verse.Gender",true)!;
        var socialNameSingleT=allNativeTypes.First(t=>t.Name=="NameSingle");
        object MakeSocialPawn(int id,string name) {
            var p=FormatterServices.GetUninitializedObject(socialPawnType);
            socialThingType.GetField("def",Flags)!.SetValue(p,socialPawnDef);
            socialThingIdField.SetValue(p,id);
            socialPawnType.GetField("kindDef",Flags)!.SetValue(p,socialKindDef);
            socialPawnType.GetField("gender",Flags)!.SetValue(p,Enum.Parse(socialGenderEnum,"Male"));
            socialPawnType.GetField("nameInt",Flags)!.SetValue(p,Activator.CreateInstance(socialNameSingleT,new object?[]{name,false}));
            var mindStateT=allNativeTypes.First(t=>t.Name=="Pawn_MindState");
            var mindState=FormatterServices.GetUninitializedObject(mindStateT);
            mindStateT.GetField("pawn",Flags)?.SetValue(mindState,p);
            var mentalHandlerT=allNativeTypes.First(t=>t.Name=="MentalStateHandler");
            var mentalHandler=FormatterServices.GetUninitializedObject(mentalHandlerT);
            mentalHandlerT.GetField("pawn",Flags)?.SetValue(mentalHandler,p);
            mindStateT.GetField("mentalStateHandler",Flags)?.SetValue(mindState,mentalHandler);
            socialPawnType.GetField("mindState",Flags)!.SetValue(p,mindState);
            return p;
        }
        var ada=MakeSocialPawn(101,"Ada"); var bo=MakeSocialPawn(102,"Bo");
        var relTrackerT=allNativeTypes.First(t=>t.Name=="Pawn_RelationsTracker");
        var directRelT=allNativeTypes.First(t=>t.Name=="DirectPawnRelation");
        var pawnRelDefT=allNativeTypes.First(t=>t.Name=="PawnRelationDef");
        var boTracker=FormatterServices.GetUninitializedObject(relTrackerT);
        relTrackerT.GetField("pawn",Flags)!.SetValue(boTracker,bo);
        relTrackerT.GetField("directRelations",Flags)!.SetValue(boTracker,Activator.CreateInstance(typeof(List<>).MakeGenericType(directRelT)));
        socialPawnType.GetField("relations",Flags)!.SetValue(bo,boTracker);
        var adaTracker=FormatterServices.GetUninitializedObject(relTrackerT);
        relTrackerT.GetField("pawn",Flags)!.SetValue(adaTracker,ada);
        var rivalDef=FormatterServices.GetUninitializedObject(pawnRelDefT);
        pawnRelDefT.GetField("defName")!.SetValue(rivalDef,"Rival");
        pawnRelDefT.GetField("opinionOffset")!.SetValue(rivalDef,-10);
        var rivalRelation=FormatterServices.GetUninitializedObject(directRelT);
        directRelT.GetField("def")!.SetValue(rivalRelation,rivalDef);
        directRelT.GetField("otherPawn")!.SetValue(rivalRelation,bo);
        directRelT.GetField("startTicks")!.SetValue(rivalRelation,0);
        var adaDirectRelations=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(directRelT))!;
        adaDirectRelations.Add(rivalRelation);
        relTrackerT.GetField("directRelations",Flags)!.SetValue(adaTracker,adaDirectRelations);
        socialPawnType.GetField("relations",Flags)!.SetValue(ada,adaTracker);

        var needsTrackerT=native.GetType("RimWorld.Pawn_NeedsTracker",true)!;
        var moodT=native.GetType("RimWorld.Need_Mood",true)!;
        var thoughtHandlerT=native.GetType("RimWorld.ThoughtHandler",true)!;
        var memHandlerT=native.GetType("RimWorld.MemoryThoughtHandler",true)!;
        var memType=native.GetType("RimWorld.Thought_Memory",true)!;
        var sitHandlerT=native.GetType("RimWorld.SituationalThoughtHandler",true)!;
        var sitT=native.GetType("RimWorld.Thought_Situational",true)!;
        var needsTrackerInst=FormatterServices.GetUninitializedObject(needsTrackerT);
        var moodInst=FormatterServices.GetUninitializedObject(moodT);
        var thoughtHandlerInst=FormatterServices.GetUninitializedObject(thoughtHandlerT);
        var memHandlerInst=FormatterServices.GetUninitializedObject(memHandlerT);
        var memoriesList=Activator.CreateInstance(typeof(List<>).MakeGenericType(memType))!;
        memHandlerT.GetField("memories",Flags)!.SetValue(memHandlerInst,memoriesList);
        var sitHandlerInst=FormatterServices.GetUninitializedObject(sitHandlerT);
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

        var socialColonists=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(socialPawnType))!;
        socialColonists.Add(ada); socialColonists.Add(bo);
        var socialMethod=detailsType.GetMethod("Social",Flags)!;

        var socialResultDirty=socialMethod.Invoke(null,new object?[]{ada,socialColonists})!;
        Check((bool)Get(socialResultDirty,"SituationalCacheStale"),"stale situational cache reflected when thoughtsDirty is true");

        sitHandlerT.GetField("thoughtsDirty",Flags)!.SetValue(sitHandlerInst,false);
        var socialResultFresh=socialMethod.Invoke(null,new object?[]{ada,socialColonists})!;
        Check(!(bool)Get(socialResultFresh,"SituationalCacheStale"),"fresh situational cache reflected when thoughtsDirty is false");

        var relations=((IEnumerable)Get(socialResultFresh,"Relations")).Cast<object>().ToArray();
        var rival=relations.FirstOrDefault(r=>(string)Get(r,"RelationDefName")=="Rival");
        Check(rival!=null,"populated direct relation is projected by defName");
        Check(rival!=null&&(bool)Get(rival,"OpinionReconstructed"),"relation opinion is marked reconstructed, not read via OpinionOf");
        // NOTE: GetRelations(a,b)'s own numeric opinion accumulation NREs in this offline
        // harness (DefDatabase<PawnRelationDef> is empty outside the real game's def-loading
        // pipeline), so ReconstructedOpinion's try/catch silently yields 0 regardless of the
        // fixture's opinionOffset. We only assert the direct-relation defName wiring here,
        // which does not depend on GetRelations.
        Check(((IEnumerable)Get(socialResultFresh,"Memories")).Cast<object>().Count()==0,"present-but-empty memories yields zero rows, not a Missing issue");
        Check(((IEnumerable)Get(socialResultFresh,"Situational")).Cast<object>().Count()==0,"present-but-empty situational cache yields zero rows, not a Missing issue");
        var socialIssues=((IEnumerable)Get(socialResultFresh,"Issues")).Cast<object>().ToArray();
        Check(!socialIssues.Any(i=>(string)Get(i,"Field")=="memories"||(string)Get(i,"Field")=="situational"),"present-but-empty thought trackers report no Missing issues");

        // Regression guard: Social() must never mutate the underlying relation/thought
        // fixtures (i.e. never call Pawn_RelationsTracker.OpinionOf or recalculate
        // situational thoughts, both of which delete/recreate memories as a side effect
        // in real RimWorld -- see PawnConfigTool's class remarks).
        Check(adaDirectRelations.Count==1&&ReferenceEquals(((IList)relTrackerT.GetField("directRelations",Flags)!.GetValue(adaTracker)!)[0],rivalRelation),"direct relation fixture is unchanged after Social()");
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
        var healthTrackerInst=FormatterServices.GetUninitializedObject(healthTrackerT);
        healthTrackerT.GetField("pawn",Flags)!.SetValue(healthTrackerInst,corpse);
        var pawnHealthStateT=native.GetType("Verse.PawnHealthState",true)!;
        healthTrackerT.GetField("healthState",Flags)!.SetValue(healthTrackerInst,Enum.Parse(pawnHealthStateT,"Dead"));
        var hediffSetT=native.GetType("Verse.HediffSet",true)!;
        var hsInst=FormatterServices.GetUninitializedObject(hediffSetT);
        hediffSetT.GetField("pawn",Flags)!.SetValue(hsInst,corpse);
        hediffSetT.GetField("hediffs",Flags)!.SetValue(hsInst,Activator.CreateInstance(typeof(List<>).MakeGenericType(native.GetType("Verse.Hediff",true)!)));
        healthTrackerT.GetField("hediffSet",Flags)!.SetValue(healthTrackerInst,hsInst);
        var billStackT=native.GetType("RimWorld.BillStack",true)!;
        var billStackInst=FormatterServices.GetUninitializedObject(billStackT);
        billStackT.GetField("bills",Flags)!.SetValue(billStackInst,Activator.CreateInstance(typeof(List<>).MakeGenericType(native.GetType("RimWorld.Bill",true)!)));
        healthTrackerT.GetField("surgeryBills",Flags)!.SetValue(healthTrackerInst,billStackInst);
        socialPawnType.GetField("health",Flags)!.SetValue(corpse,healthTrackerInst);
        var corpseColonists=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(socialPawnType))!;
        corpseColonists.Add(corpse);
        var corpseRow=Wire("PawnState","{\"needs\":{\"mood\":0},\"health\":{\"summaryFraction\":1}}");
        var corpseDetail=Wire("PawnDetails","{\"needs\":true,\"health\":false,\"equipment\":true,\"biography\":false,\"settings\":true,\"social\":false,\"animals\":false}");
        detailsType.GetMethod("Apply",Flags)!.Invoke(null,new object?[]{corpse,corpseColonists,corpseRow,corpseDetail});
        Check(Get(corpseRow,"Needs")!=null,"corpse needs projection still populates for a dead pawn");
        Check(Get(corpseRow,"Equipment")!=null,"corpse equipment projection still populates for a dead pawn");
        Check(Get(corpseRow,"Settings")!=null,"corpse settings projection still populates for a dead pawn");
        Check(Get(corpseRow,"Health")==null&&Get(corpseRow,"Biography")==null,"corpse health/biography stay disabled (Blocker: ModsConfig static cctor)");
        var corpseNeedsIssues=((IEnumerable)Get(Get(corpseRow,"Needs"),"Issues")).Cast<object>().ToArray();
        foreach(var field in new[]{"food","hunger_category","rest","joy"})
            Check(corpseNeedsIssues.Any(i=>(string)Get(i,"Field")==field&&Get(Get(i,"Unavailable"),"Reason").ToString()=="NativeComponentMissing"),"corpse without a needs tracker reports missing "+field);
        var corpseIssues=((IEnumerable)Get(corpseRow,"Issues")).Cast<object>().ToArray();
        foreach(var field in new[]{"health","biography","social","animal_state"})
            Check(corpseIssues.Any(i=>(string)Get(i,"Field")==field&&Get(Get(i,"Unavailable"),"Reason").ToString()=="NotRequested"),"corpse detail explicitly disables blocked-path section "+field);

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
