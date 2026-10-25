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

    private static bool Throws(Action action,string name) {try {action();return false;}catch(TargetInvocationException e){return e.InnerException!.GetType().Name==name;}}
    internal static int Invoke(string[] args)
    {
        var directories=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{
            var path=directories.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);
            return path==null?null:Assembly.LoadFrom(path);
        };
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies()) Assembly.Load(reference);
        tools=bridge.GetType("RimGovernor.Native.NativePawnObservationTools",true)!;
        WorkSettingsProof.Run(bridge, Check);
        ZoneProof.Run(bridge, Check);
        var identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 };
        var query = new RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity }
        };
        bool Valid(RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest? value) => (bool)tools.GetMethod("Validate",Flags)!.Invoke(null,new object?[]{value,null})!;
        Check(Valid(query), "default pawn filter accepted");
        var filter = new RimGovernor.Brain.GameFacts.Observations.PawnFilter {
            Colonist = RimGovernor.Brain.GameFacts.Observations.PawnMembership.Excluded,
            WithinColonistDistance = new RimGovernor.Brain.GameFacts.Observations.ColonistRange.Within { Distance = 0 }
        };
        Check(Valid(query with { Filter = filter }), "explicit zero range and excluded membership accepted");
        Check(!Valid(null) && !Valid(query with { Scope = null }), "identity scope required");
        foreach (var bad in new[] {
            filter with { Ids = ["Pawn1", "Pawn1"] }, filter with { Ids = [""] },
            filter with { NameContains = new string('x',257) }, filter with { NameContains = "bad\0name" },
            filter with { WithinColonistDistance = new RimGovernor.Brain.GameFacts.Observations.ColonistRange.Within { Distance = -1 } },
            filter with { WithinColonistDistance = new RimGovernor.Brain.GameFacts.Observations.ColonistRange.Within { Distance = double.NaN } },
            filter with { WithinColonistDistance = new RimGovernor.Brain.GameFacts.Observations.ColonistRange.Within { Distance = double.PositiveInfinity } }
        }) Check(!Valid(query with { Filter = bad }), "invalid exact pawn filter refused");
        Check(Valid(query with { Filter = filter with { Ids = [.. Enumerable.Range(0,256).Select(i=>"Pawn"+i)] } }), "256 exact IDs accepted");
        Check(!Valid(query with { Filter = filter with { Ids = [.. Enumerable.Range(0,257).Select(i=>"Pawn"+i)] } }), "257 exact IDs refused");
        // Hunt weapon classification is now Catalog.HuntWeapon, compiler-bound to RimWorld definitions;
        // AcquisitionObservationTests exercises its consumer's hunter/prey gate using those weapon facts.
        // The filter is a predicate over the native pawn, applied before any row is built (#3768); it reads live
        // Verse.Pawn facts this offline harness cannot construct, so native acceptance covers it. Every row is
        // read whole: there are no detail flags to default or opt out of.
        var detailsType=bridge.GetType("RimGovernor.Native.NativePawnDetails",true)!;
        var native=Assembly.Load("Assembly-CSharp");
        // Backstory labels are no longer a published pawn fact.
        var emptyPawn=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(native.GetType("Verse.Pawn",true)!);
        // Equipment reads live MeleeDPS; an uninitialized offline pawn cannot supply that projection.
        Check(detailsType.GetMethod("Care",Flags)!.Invoke(null,new[]{emptyPawn}) is RimGovernor.Brain.GameFacts.Observations.PawnCare.None,"a pawn without player settings has no care policy");
        Check(detailsType.GetMethod("Work",Flags)!.Invoke(null,new[]{emptyPawn}) is RimGovernor.Brain.GameFacts.Observations.PawnWork.NeverWorks,"absent work tracker publishes NeverWorks");
        Check(((IEnumerable)detailsType.GetMethod("Schedule",Flags)!.Invoke(null,new[]{emptyPawn})!).Cast<object>().Count()==0,"a pawn without a timetable has no slots");

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

        // Social also reads live expectations; exercise its side-effect-free thought readers offline.
        var settingsRead=bridge.GetType("RimGovernor.Native.PawnSettingsRead",true)!;
        var socialMethod=settingsRead.GetMethod("LiveSituational",Flags)!;

        var socialResultDirty=socialMethod.Invoke(null,new object?[]{ada})!;
        Check((bool)((System.Runtime.CompilerServices.ITuple)socialResultDirty)[1]!,"stale situational cache reflected when thoughtsDirty is true");

        sitHandlerT.GetField("thoughtsDirty",Flags)!.SetValue(sitHandlerInst,false);
        var socialResultFresh=socialMethod.Invoke(null,new object?[]{ada})!;
        Check(!(bool)((System.Runtime.CompilerServices.ITuple)socialResultFresh)[1]!,"fresh situational cache reflected when thoughtsDirty is false");

        Check(((IEnumerable)settingsRead.GetMethod("LiveMemories",Flags)!.Invoke(null,new[]{ada})!).Cast<object>().Count()==0,"present-but-empty memories yields zero rows");
        Check(((IEnumerable)((System.Runtime.CompilerServices.ITuple)socialResultFresh)[0]!).Cast<object>().Count()==0,"present-but-empty situational cache yields zero rows");

        // Regression guard: The thought readers must never mutate the thought fixtures (never
        // recalculate situational thoughts, which deletes/recreates memories as a side
        // effect in real RimWorld -- see PawnSettingsRead's class remarks).
        Check(ReferenceEquals(memHandlerT.GetField("memories",Flags)!.GetValue(memHandlerInst),memoriesList)&&memoriesList is IList{Count:0},"memories list reference and emptiness unchanged after reading thoughts");
        Check(ReferenceEquals(sitHandlerT.GetField("cachedThoughts",Flags)!.GetValue(sitHandlerInst),cachedThoughtsList)&&cachedThoughtsList is IList{Count:0},"cached situational thoughts reference and emptiness unchanged after reading thoughts");

        var number=tools.GetMethod("Number",Flags)!;
        Check((double)number.Invoke(null,new object[]{0d})! == 0,"observed zero retained");
        Check(Throws(()=>number.Invoke(null,new object[]{double.NaN}),"InvalidOperationException"),"nonfinite native fact fails");
        Check(Throws(()=>tools.GetMethod("Text",Flags)!.Invoke(null,new object[]{new string('x',4097)}),"ReadLimit"),"native labels are never silently truncated before matching");
        Console.WriteLine(checks+" compiled pawn boundary/projection assertions passed; no gameplay assertions.");return 0;
    }
}
