#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Common=RimGovernor.Protocol.Common;
using Receipts=RimGovernor.Protocol.Receipts;
namespace HomeBridge.BridgeTools {
 internal sealed class NativeProductionRecord {
  internal readonly Thing Bench;internal readonly IBillGiver Giver;internal readonly Bill_Production Bill;internal readonly Map Map;internal readonly string Before;
  internal readonly Dictionary<Thing,int> Outputs=new Dictionary<Thing,int>();
  // Outputs seen spawned and player-accessible on some observe; later consumption or hauling does not unobserve them.
  internal readonly HashSet<Thing> Seen=new HashSet<Thing>();
  internal string Config="";internal int Index=-1;internal uint Iterations;internal long Generated;internal bool Unreadable;
  internal NativeProductionRecord(Thing bench,IBillGiver giver,Bill_Production bill,string before){Bench=bench;Giver=giver;Bill=bill;Before=before;Map=bench.Map;}
  internal void Capture(){Config=NativeProductionBills.Configuration(Bill);Index=Giver.BillStack.IndexOf(Bill);}
  internal Receipts.BillEffect Evidence(Common.ObservationContext context){
   var present=Bench.Spawned&&Bench.Map==Map&&Giver.BillStack.Bills.Contains(Bill);var total=Outputs.Values.Sum(v=>(long)v);
   var result=new Receipts.BillEffect{Stack=new Receipts.SnapshotEvidence{EntityId=Bench.GetUniqueLoadID(),BeforeToken=Before},BillId=Bill.GetUniqueLoadID(),RecipeDef=Bill.recipe.defName,Present=present,Index=present?Giver.BillStack.IndexOf(Bill):-1,ConfigurationMatches=present&&Index>=0&&Giver.BillStack.IndexOf(Bill)==Index&&NativeProductionBills.Configuration(Bill)==Config,Iterations=Iterations,OutputComplete=NativeProductionTracking.Ready&&!Unreadable&&total==Generated,OutputObserved=NativeProductionTracking.Ready&&!Unreadable&&total==Generated&&Outputs.Count>0};
   if(present)result.Stack.AfterToken=NativeProductionBills.Snapshot(Bench,Giver,context).Token;
   foreach(var bill in Giver.BillStack.Bills)result.OrderedBillIds.Add(bill.GetUniqueLoadID());
   foreach(var pair in Outputs.OrderBy(p=>p.Key.GetUniqueLoadID(),StringComparer.Ordinal)){var thing=pair.Key;result.Outputs.Add(new Receipts.ProductionOutput{ThingId=thing.GetUniqueLoadID(),DefName=thing.def.defName,Units=pair.Value});if(!thing.Destroyed&&thing.Spawned&&thing.Map==Map&&thing.stackCount>=pair.Value&&!thing.IsForbidden(Faction.OfPlayer)&&!thing.Position.Fogged(Map))Seen.Add(thing);if(!Seen.Contains(thing))result.OutputObserved=false;}
   return result;
  }
  internal Receipts.Progress Observe(Common.AttemptKey attempt,Common.ObservationContext context){var result=new Receipts.Progress{Attempt=attempt.Clone(),Context=context.Clone(),CompleteInspection=true};var value=Evidence(context);var evidence=new Receipts.EffectEvidence{Bill=value};
   if(!value.Present){result.CompleteInspection=false;result.Unknown=new Receipts.UnknownEffect{Reason="Original bill unavailable."};}
   else if(!value.ConfigurationMatches)result.Unsuccessful=new Receipts.UnsuccessfulEffect{Reason=Receipts.UnsuccessfulReason.OutcomeNotAchieved,Evidence=evidence,Detail="Original bill configuration changed."};
   else if(value.Iterations>0&&value.OutputComplete&&value.OutputObserved)result.Completed=new Receipts.CompletedEffect{Evidence=evidence};
   else if(value.Iterations>0&&value.OutputComplete&&value.Outputs.Count==0)result.Unsuccessful=new Receipts.UnsuccessfulEffect{Reason=Receipts.UnsuccessfulReason.OutcomeNotAchieved,Evidence=evidence,Detail="Ordinary bill iteration produced no item."};
   else if(value.Iterations==0)result.Pending=new Receipts.PendingEffect{Evidence=evidence};
   else {result.CompleteInspection=false;result.Unknown=new Receipts.UnknownEffect{Reason="Produced items require complete placement observation."};}
   return result;
  }
 }
 internal static class NativeProductionTracking {
  private const string Owner="rimgovernor.native.production";
  private sealed class State {internal readonly Dictionary<Bill,NativeProductionRecord>Bills=new Dictionary<Bill,NativeProductionRecord>();internal readonly Dictionary<Thing,NativeProductionRecord>Products=new Dictionary<Thing,NativeProductionRecord>();internal readonly Dictionary<Thing,NativeProductionRecord>Placed=new Dictionary<Thing,NativeProductionRecord>();}
  private static readonly ConditionalWeakTable<Game,State> States=new ConditionalWeakTable<Game,State>();private static readonly List<MethodBase> Targets=new List<MethodBase>();private static bool installed;
  internal static void Install(){if(installed)return;installed=true;try{var harmony=new Harmony(Owner);
   Patch(harmony,AccessTools.Method(typeof(GenRecipe),"MakeRecipeProducts",new[]{typeof(RecipeDef),typeof(Pawn),typeof(List<Thing>),typeof(Thing),typeof(IBillGiver),typeof(Precept_ThingStyle),typeof(ThingStyleDef),typeof(int?)}),null,nameof(Products));
   Patch(harmony,AccessTools.Method(typeof(Bill_Production),"Notify_IterationCompleted",new[]{typeof(Pawn),typeof(List<Thing>)}),null,nameof(Finished));
   Patch(harmony,AccessTools.Method(typeof(GenPlace),"TryPlaceThing",new[]{typeof(Thing),typeof(IntVec3),typeof(Map),typeof(ThingPlaceMode),typeof(Thing).MakeByRefType(),typeof(Action<Thing,int>),typeof(Predicate<IntVec3>),typeof(Rot4?),typeof(int)}),nameof(BeforePlace),null);
   Patch(harmony,AccessTools.Method(typeof(Thing),nameof(Thing.TryAbsorbStack),new[]{typeof(Thing),typeof(bool)}),nameof(BeforeAbsorb),nameof(AfterAbsorb));
  }catch(Exception e){Log.Error("[RimGovernor] Production observation unavailable: "+e);}}
  private static void Patch(Harmony harmony,MethodBase? target,string? prefix,string? postfix){if(target==null)throw new MissingMethodException("Production hook missing");harmony.Patch(target,prefix==null?null:new HarmonyMethod(typeof(NativeProductionTracking),prefix),postfix==null?null:new HarmonyMethod(typeof(NativeProductionTracking),postfix));Targets.Add(target);}
  internal static bool Ready=>Targets.Count==4&&Targets.All(t=>{var p=Harmony.GetPatchInfo(t);return p!=null&&p.Prefixes.Concat(p.Postfixes).Any(h=>h.owner==Owner);});
  internal static bool Track(NativeProductionRecord record){if(!Ready||Current.Game==null)return false;var state=States.GetOrCreateValue(Current.Game);if(state.Bills.Count>=4096||state.Bills.ContainsKey(record.Bill))return false;state.Bills.Add(record.Bill,record);return true;}
  private static void Products(Pawn __1,ref IEnumerable<Thing> __result){if(Current.Game==null||!States.TryGetValue(Current.Game,out var state)||__1.CurJob?.bill==null||!state.Bills.TryGetValue(__1.CurJob.bill,out var record)||record.Iterations>0)return;__result=ObserveProducts(__result,state,record);}
  private static IEnumerable<Thing> ObserveProducts(IEnumerable<Thing> products,State state,NativeProductionRecord record){
   using(var iterator=products.GetEnumerator()){while(true){Thing current;try{if(!iterator.MoveNext())break;current=iterator.Current;if(NativeProductionBills.Product(current.def)){if(state.Products.Count>=4096||current.stackCount<=0)record.Unreadable=true;else{state.Products[current]=record;record.Generated=checked(record.Generated+current.stackCount);}}}catch{record.Unreadable=true;throw;}yield return current;}}
  }
  private static void Finished(Bill_Production __instance){if(Current.Game!=null&&States.TryGetValue(Current.Game,out var state)&&state.Bills.TryGetValue(__instance,out var record)&&record.Iterations==0)record.Iterations=1;}
  private static void BeforePlace(Thing __0,ref Action<Thing,int>? __5){if(Current.Game==null||!States.TryGetValue(Current.Game,out var state)||!state.Products.TryGetValue(__0,out var record))return;state.Products.Remove(__0);var original=__5;__5=(thing,count)=>{try{original?.Invoke(thing,count);if(count<=0||record.Outputs.Count>=256&&!record.Outputs.ContainsKey(thing)||!NativeProductionBills.Product(thing.def)){record.Unreadable=true;return;}record.Outputs.TryGetValue(thing,out var old);record.Outputs[thing]=checked(old+count);state.Placed[thing]=record;}catch{record.Unreadable=true;throw;}};}
  // A placed output hauled onto another stack is absorbed and destroyed; its units follow the absorbing stack so the merge stays observable (#438).
  private static void BeforeAbsorb(Thing __instance,Thing other,out int __state){__state=other.stackCount;}
  private static void AfterAbsorb(Thing __instance,Thing other,int __state){if(Current.Game==null||!States.TryGetValue(Current.Game,out var state)||!state.Placed.TryGetValue(other,out var record)||!record.Outputs.TryGetValue(other,out var units))return;
   try{var absorbed=__state-(other.Destroyed?0:other.stackCount);if(absorbed<=0)return;var moved=Math.Min(absorbed,units);if(state.Placed.TryGetValue(__instance,out var existing)&&existing!=record||record.Outputs.Count>=256&&!record.Outputs.ContainsKey(__instance)){record.Unreadable=true;return;}
    if(moved==units){record.Outputs.Remove(other);state.Placed.Remove(other);if(record.Seen.Remove(other))record.Seen.Add(__instance);}else record.Outputs[other]=units-moved;
    record.Outputs.TryGetValue(__instance,out var old);record.Outputs[__instance]=checked(old+moved);state.Placed[__instance]=record;}catch{record.Unreadable=true;throw;}}
 }
}
