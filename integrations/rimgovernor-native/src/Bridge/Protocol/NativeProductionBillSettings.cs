using System;
using Operations=RimGovernor.Brain.GameFacts.Operations;
using Common=RimGovernor.Brain.GameFacts.Common;
namespace RimGovernor.Native {
 internal static class NativeProductionBillSettings {
  internal static bool Valid(RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent? command){
   if(command==null||!GameThread.IsIdentifier(command.BenchId)||!GameThread.IsIdentifier(command.RecipeDef)||command.Settings is not { } s)return false;
   if(command.ReplaceOwnedBill!=null&&!GameThread.IsIdentifier(command.ReplaceOwnedBill.Id))return false;
   if(!ValidIngredients(s.Ingredients))return false;
   bool worker=s.Worker==null||s.Worker.Value is RimGovernor.Brain.GameOrders.Operations.AssignmentValue.EntityIdCase{Value:var id}&&GameThread.IsIdentifier(id);
   if(!worker)return false;
   if(CorpseRecipe(command.RecipeDef)){
    if(s.Corpses is not RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.Selected{Class:var of}||!Enum.IsDefined(of))return false;
    return s.Repetition is RimGovernor.Brain.GameOrders.Operations.BillRepetition.Forever&&!s.BeerReserve&&s.Ingredients==null&&(of!=RimGovernor.Brain.GameFacts.Common.CorpseClass.Animal)==(s.Worker!=null);
   }
   if(s.Corpses is not RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.NotApplicable)return false;
   return s.Repetition switch {
    RimGovernor.Brain.GameOrders.Operations.BillRepetition.Count{Repetitions:var count}=>count>=1&&!s.BeerReserve,
    RimGovernor.Brain.GameOrders.Operations.BillRepetition.Target{Quantity:var target,UnpauseThreshold:var threshold}=>target>=1&&threshold==Math.Max(1,target/2)&&s.Worker==null,
    _=>false
   };
  }
  internal static bool CorpseRecipe(string recipe)=>NativeRecipeRoles.Corpse(NativeRecipeRoles.Named(recipe));
  internal static bool ValidIngredients(RimGovernor.Brain.GameOrders.Operations.FilterPatch? filter){
   if(filter==null)return true;
   if(filter.Allow.Count!=0||filter.Disallow.Count!=0||filter.HitPoints is not RimGovernor.Brain.GameOrders.Operations.HitPointRange.Preserve||filter.QualityMin is not null||filter.QualityMax is not null||filter.Replace==null||filter.Replace.Selectors.Count==0||!filter.Equals(new RimGovernor.Brain.GameOrders.Operations.FilterPatch{Replace=filter.Replace, HitPoints = new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Preserve() }))return false;
   string? previous=null;
   foreach(var selector in filter.Replace.Selectors){
    if(selector.Definition is not RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.ThingDefCase{Value:var thingDef}||!GameThread.IsIdentifier(thingDef)||!selector.Equals(new RimGovernor.Brain.GameOrders.Operations.FilterSelector{Definition=new RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.ThingDefCase(thingDef)})||previous!=null&&StringComparer.Ordinal.Compare(previous,thingDef)>=0)return false;
    previous=thingDef;
   }
   return true;
  }
 }
}
