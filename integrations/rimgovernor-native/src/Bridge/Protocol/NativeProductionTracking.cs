#nullable enable
using RimGovernor.Host.Sdk;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
namespace HomeBridge.BridgeTools {
 // One bill a production bill intent placed: the configuration it was
 // written with, so the bill census can say whether it still reads as
 // placed (ManagedUnchanged), and whether its products need trade protection.
 internal sealed class NativeProductionRecord {
  internal readonly IBillGiver Giver;internal readonly Bill_Production Bill;
  internal bool TradeHumanFood=>Bill.recipe.products.Any(p=>p.thingDef==ThingDefOf.MealSurvivalPack)&&Bill.ingredientFilter.AllowedThingDefs.Any(HumanFoodFacts.IsHumanMeat);
  internal bool Retired;
  internal string Config="";internal int Index=-1;
  internal NativeProductionRecord(IBillGiver giver,Bill_Production bill){Giver=giver;Bill=bill;}
  internal void Capture(){Config=NativeProductionBills.Configuration(Bill);Index=Giver.BillStack.IndexOf(Bill);}
 }
 internal static class NativeProductionTracking {
  private const string Owner="rimgovernor.native.production";
  private sealed class State {internal readonly Dictionary<Bill,NativeProductionRecord>Bills=new Dictionary<Bill,NativeProductionRecord>();}
  private static readonly ConditionalWeakTable<Game,State> States=new ConditionalWeakTable<Game,State>();private static MethodBase? target;private static bool installed;
  internal static void Install(){if(installed)return;installed=true;try{var harmony=new Harmony(Owner);
   target=AccessTools.Method(typeof(GenRecipe),"MakeRecipeProducts",new[]{typeof(RecipeDef),typeof(Pawn),typeof(List<Thing>),typeof(Thing),typeof(IBillGiver),typeof(Precept_ThingStyle),typeof(ThingStyleDef),typeof(int?)});
   if(target==null)throw new MissingMethodException("Production hook missing");
   harmony.Patch(target,null,new HarmonyMethod(typeof(NativeProductionTracking),nameof(Products)));
  }catch(Exception e){ModLog.Error("production", "Production tracking unavailable: "+e);}}
  internal static bool Ready=>target!=null&&Harmony.GetPatchInfo(target)?.Postfixes.Any(h=>h.owner==Owner)==true;
  internal static bool ManagedUnchanged(Bill bill)=>Current.Game!=null&&States.TryGetValue(Current.Game,out var state)&&state.Bills.TryGetValue(bill,out var r)&&!r.Retired&&OrdinaryMeal(bill.recipe)&&r.Giver.BillStack.Bills.Contains(bill)&&r.Index==r.Giver.BillStack.IndexOf(bill)&&r.Config==NativeProductionBills.Configuration(bill);
  // Replacement adopts any ordinary meal bill on the map by id, whoever wrote it (#461): a bill edited under Manual is
  // evidence of an old order, not authority over the tier Auto plans. A tracked one is retired when replaced.
  internal static bool OrdinaryMeal(RecipeDef recipe)=>recipe.products.Count==1&&recipe.products[0].thingDef.ingestible!=null&&recipe.products[0].thingDef.ingestible.preferability>=FoodPreferability.MealSimple&&recipe.products[0].thingDef.ingestible.preferability<=FoodPreferability.MealLavish;
  internal static Bill? ReplaceableBill(string id,Thing bench,string recipe)=>bench.Map.listerThings.AllThings.OfType<IBillGiver>().SelectMany(g=>g.BillStack.Bills).FirstOrDefault(b=>b.GetUniqueLoadID()==id && (b.recipe.defName==recipe && b.billStack==(bench as IBillGiver)?.BillStack || b.recipe.defName!=recipe && OrdinaryMeal(b.recipe)));
  internal static void Retire(Bill bill){if(Current.Game!=null&&States.TryGetValue(Current.Game,out var state)&&state.Bills.TryGetValue(bill,out var r))r.Retired=true;}
  internal static bool Track(NativeProductionRecord record){if(!Ready||Current.Game==null)return false;var state=States.GetOrCreateValue(Current.Game);if(state.Bills.Count>=4096||state.Bills.ContainsKey(record.Bill))return false;state.Bills.Add(record.Bill,record);return true;}
  private static void Products(Pawn __1,ref IEnumerable<Thing> __result){if(Current.Game==null||!States.TryGetValue(Current.Game,out var state)||__1.CurJob?.bill==null||!state.Bills.TryGetValue(__1.CurJob.bill,out var record))return;if(record.TradeHumanFood)__result=ProtectTradeProducts(__result);}
  private static IEnumerable<Thing> ProtectTradeProducts(IEnumerable<Thing> products){foreach(var thing in products){if(HumanFoodFacts.ContainsHumanMeat(thing))thing.SetForbidden(true,false);yield return thing;}}
 }
}
