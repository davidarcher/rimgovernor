#nullable enable
using System;
using Operations=RimGovernor.Contracts.Wire.Operations;
using Common=RimGovernor.Contracts.Wire.Common;
namespace HomeBridge.BridgeTools {
 internal static class NativeProductionBillSettings {
  internal static bool Valid(Operations.ProductionBillIntent? command){
   if(command==null||!ProtoBoundary.IsIdentifier(command.BenchId)||!ProtoBoundary.IsIdentifier(command.RecipeDef)||command.Settings is not { } s)return false;
   if(command.ReplaceOwnedBill!=null&&!ProtoBoundary.IsIdentifier(command.ReplaceOwnedBill.Id))return false;
   if(!ValidIngredients(s.Ingredients))return false;
   bool worker=s.Worker==null||s.Worker.Value is Operations.AssignmentValue.EntityIdCase{Value:var id}&&ProtoBoundary.IsIdentifier(id);
   if(!worker)return false;
   if(CorpseRecipe(command.RecipeDef!)){
    if(s.Corpses is not Operations.BillCorpseClass.Selected{Class:var of}||!Enum.IsDefined(of))return false;
    return s.Repetition is Operations.BillRepetition.Forever&&!s.BeerReserve&&s.Ingredients==null&&(of!=Common.CorpseClass.Animal)==(s.Worker!=null);
   }
   if(s.Corpses is not Operations.BillCorpseClass.NotApplicable)return false;
   return s.Repetition switch {
    Operations.BillRepetition.Count{Repetitions:var count}=>count>=1&&!s.BeerReserve,
    Operations.BillRepetition.Target{Quantity:var target,UnpauseThreshold:var threshold}=>target>=1&&threshold==Math.Max(1,target/2)&&s.Worker==null,
    _=>false
   };
  }
  internal static bool CorpseRecipe(string recipe)=>NativeRecipeRoles.Corpse(NativeRecipeRoles.Named(recipe));
  internal static bool ValidIngredients(Operations.FilterPatch? filter){
   if(filter==null)return true;
   if(filter.Allow.Count!=0||filter.Disallow.Count!=0||filter.HitPoints is not Operations.HitPointRange.Preserve||filter.QualityMin is not null||filter.QualityMax is not null||filter.Replace==null||filter.Replace.Selectors.Count==0||!filter.Equals(new Operations.FilterPatch{Replace=filter.Replace, HitPoints = new Operations.HitPointRange.Preserve() }))return false;
   string? previous=null;
   foreach(var selector in filter.Replace.Selectors){
    if(selector.Definition is not Operations.FilterSelectorDefinition.ThingDefCase{Value:var thingDef}||!ProtoBoundary.IsIdentifier(thingDef)||!selector.Equals(new Operations.FilterSelector{Definition=new Operations.FilterSelectorDefinition.ThingDefCase(thingDef)})||previous!=null&&StringComparer.Ordinal.Compare(previous,thingDef)>=0)return false;
    previous=thingDef;
   }
   return true;
  }
 }
}
