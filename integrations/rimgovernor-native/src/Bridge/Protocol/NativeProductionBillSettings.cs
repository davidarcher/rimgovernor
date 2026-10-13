#nullable enable
using System;
using Operations=RimGovernor.Contracts.Wire.Operations;
using Common=RimGovernor.Contracts.Wire.Common;
namespace HomeBridge.BridgeTools {
 internal static class NativeProductionBillSettings {
  // Wire records keep presence of optional scalars; the exact-shape comparisons below
  // treat an unset optional as its default (the generated protobuf Equals did).
  private static Operations.BillSettings Norm(Operations.BillSettings b)=>b with{RepeatMode=b.RepeatMode??Operations.RepeatMode.Unspecified,RepeatCount=b.RepeatCount??0,TargetCount=b.TargetCount??0,UnpauseThreshold=b.UnpauseThreshold??0,PauseWhenSatisfied=b.PauseWhenSatisfied??false,Suspended=b.Suspended??false,IngredientSearchRadius=b.IngredientSearchRadius??0f,SkillMin=b.SkillMin??0,SkillMax=b.SkillMax??0,BeerReserve=b.BeerReserve??false,CorpseClass=b.CorpseClass??Common.CorpseClass.Unspecified};
  internal static bool Valid(Operations.ProductionBillIntent? command){
   var s=command?.Settings;
   if(command?.ReplaceOwnedBill!=null && !ProtoBoundary.IsIdentifier(command.ReplaceOwnedBill.Id))return false;
   if(!ValidIngredients(s?.Ingredients))return false;
   if(command==null||command.BenchId is null||!ProtoBoundary.IsIdentifier(command.BenchId)||command.RecipeDef is null||!ProtoBoundary.IsIdentifier(command.RecipeDef)||s==null)return false;
   var expected=new Operations.BillSettings{RepeatMode=s.RepeatMode,TargetCount=s.TargetCount,UnpauseThreshold=s.UnpauseThreshold,PauseWhenSatisfied=s.PauseWhenSatisfied,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Destination=new Operations.BillStoreDestination.ModeCase(Operations.StoreMode.DropOnFloor)},Ingredients=s.Ingredients};
   if(s.BeerReserve is not null){if(!s.BeerReserve.GetValueOrDefault())return false;expected = expected with { BeerReserve = true };}
   // Corpse bill: a corpse recipe plus whose corpses it takes. A
   // humanlike butcher bill pins its worker.
   if(CorpseRecipe((command.RecipeDef ?? ""))){
    if(s.CorpseClass is null||!Enum.IsDefined(typeof(Common.CorpseClass),s.CorpseClass.GetValueOrDefault())||s.CorpseClass.GetValueOrDefault()==Common.CorpseClass.Unspecified)return false;
    var pinned=s.CorpseClass.GetValueOrDefault()!=Common.CorpseClass.Animal;
    if(pinned!=(s.Worker!=null)||s.Worker!=null&&(s.Worker.Value is not Operations.AssignmentValue.EntityIdCase{Value:var workerId}||!ProtoBoundary.IsIdentifier(workerId)))return false;
    var corpse=new Operations.BillSettings{RepeatMode=Operations.RepeatMode.Forever,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Destination=new Operations.BillStoreDestination.ModeCase(Operations.StoreMode.DropOnFloor)},Worker=s.Worker,CorpseClass=s.CorpseClass};
    return Norm(s).Equals(Norm(corpse));
   }
   // A finite batch (gear, sculpture): repeat a count of times, no target.
   // It may pin one worker (an art bill per artist).
   if(s.RepeatMode.GetValueOrDefault()==Operations.RepeatMode.Count)
    return s.BeerReserve is null&&s.RepeatCount is not null&&s.RepeatCount.GetValueOrDefault()>=1&&(s.Worker==null||s.Worker.Value is Operations.AssignmentValue.EntityIdCase{Value:var pinnedWorker}&&ProtoBoundary.IsIdentifier(pinnedWorker))&&Norm(s).Equals(Norm(new Operations.BillSettings{RepeatMode=Operations.RepeatMode.Count,RepeatCount=s.RepeatCount,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Destination=new Operations.BillStoreDestination.ModeCase(Operations.StoreMode.DropOnFloor)},Ingredients=s.Ingredients,Worker=s.Worker}));
   return Norm(s).Equals(Norm(expected))&&s.RepeatMode.GetValueOrDefault()==Operations.RepeatMode.Target&&s.TargetCount is not null&&s.TargetCount.GetValueOrDefault()>=1&&s.UnpauseThreshold is not null&&s.UnpauseThreshold.GetValueOrDefault()==Math.Max(1,s.TargetCount.GetValueOrDefault()/2)&&s.PauseWhenSatisfied is not null&&s.PauseWhenSatisfied.GetValueOrDefault();
  }
  internal static bool CorpseRecipe(string recipe)=>NativeRecipeRoles.Corpse(NativeRecipeRoles.Named(recipe));
  internal static bool ValidIngredients(Operations.FilterPatch? filter){
   if(filter==null)return true;
   if(filter.Allow.Count!=0||filter.Disallow.Count!=0||filter.HitPointsMin is not null||filter.HitPointsMax is not null||filter.QualityMin is not null||filter.QualityMax is not null||filter.Replace==null||filter.Replace.Selectors.Count==0||!filter.Equals(new Operations.FilterPatch{Replace=filter.Replace}))return false;
   string? previous=null;
   foreach(var selector in filter.Replace.Selectors){
    if(selector.Definition is not Operations.FilterSelectorDefinition.ThingDefCase{Value:var thingDef}||!ProtoBoundary.IsIdentifier(thingDef)||!selector.Equals(new Operations.FilterSelector{Definition=new Operations.FilterSelectorDefinition.ThingDefCase(thingDef)})||previous!=null&&StringComparer.Ordinal.Compare(previous,thingDef)>=0)return false;
    previous=thingDef;
   }
   return true;
  }
 }
}
