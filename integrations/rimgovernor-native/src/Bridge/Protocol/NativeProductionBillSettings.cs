#nullable enable
using System;
using Operations=RimGovernor.Protocol.Operations;
using Common=RimGovernor.Protocol.Common;
namespace HomeBridge.BridgeTools {
 internal static class NativeProductionBillSettings {
  internal static bool Valid(Operations.ProductionBillIntent? command){
   var s=command?.Settings;
   if(command?.HasReplaceOwnedBillId==true && !ProtoBoundary.IsIdentifier(command.ReplaceOwnedBillId))return false;
   if(!ValidIngredients(s?.Ingredients))return false;
   if(command==null||!command.HasBenchId||!ProtoBoundary.IsIdentifier(command.BenchId)||!command.HasRecipeDef||!ProtoBoundary.IsIdentifier(command.RecipeDef)||s==null)return false;
   var expected=new Operations.BillSettings{RepeatMode=s.RepeatMode,TargetCount=s.TargetCount,UnpauseThreshold=s.UnpauseThreshold,PauseWhenSatisfied=s.PauseWhenSatisfied,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Mode=Operations.StoreMode.DropOnFloor},Ingredients=s.Ingredients?.Clone()};
   if(s.HasBeerReserve){if(!s.BeerReserve)return false;expected.BeerReserve=true;}
   // Corpse bill (#833): a corpse recipe plus whose corpses it takes. A
   // humanlike butcher bill pins its worker; cremation never pins one.
   if(CorpseRecipe(command.RecipeDef)){
    if(!s.HasCorpseClass||!Enum.IsDefined(typeof(Common.CorpseClass),s.CorpseClass)||s.CorpseClass==Common.CorpseClass.Unspecified)return false;
    var pinned=command.RecipeDef=="ButcherCorpseFlesh"&&s.CorpseClass!=Common.CorpseClass.Animal;
    if(pinned!=(s.Worker!=null)||s.Worker!=null&&(s.Worker.ValueCase!=Operations.Assignment.ValueOneofCase.EntityId||!ProtoBoundary.IsIdentifier(s.Worker.EntityId)))return false;
    return s.Equals(new Operations.BillSettings{RepeatMode=Operations.RepeatMode.Forever,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Mode=Operations.StoreMode.DropOnFloor},Worker=s.Worker?.Clone(),CorpseClass=s.CorpseClass});
   }
   // A finite batch (gear, sculpture): repeat a count of times, no target.
   // It may pin one worker (#1190: an art bill per artist).
   if(s.RepeatMode==Operations.RepeatMode.Count)
    return !s.HasBeerReserve&&s.HasRepeatCount&&s.RepeatCount>=1&&s.RepeatCount<=10000&&(s.Worker==null||s.Worker.ValueCase==Operations.Assignment.ValueOneofCase.EntityId&&ProtoBoundary.IsIdentifier(s.Worker.EntityId))&&s.Equals(new Operations.BillSettings{RepeatMode=Operations.RepeatMode.Count,RepeatCount=s.RepeatCount,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Mode=Operations.StoreMode.DropOnFloor},Ingredients=s.Ingredients?.Clone(),Worker=s.Worker?.Clone()});
   return s.Equals(expected)&&s.RepeatMode==Operations.RepeatMode.Target&&s.HasTargetCount&&s.TargetCount>=1&&s.TargetCount<=10000&&s.HasUnpauseThreshold&&s.UnpauseThreshold==Math.Max(1,s.TargetCount/2)&&s.HasPauseWhenSatisfied&&s.PauseWhenSatisfied;
  }
  internal static bool CorpseRecipe(string recipe)=>recipe=="ButcherCorpseFlesh"||recipe=="CremateCorpse";
  internal static bool ValidIngredients(Operations.FilterPatch? filter){
   if(filter==null)return true;
   if(filter.Allow.Count!=0||filter.Disallow.Count!=0||filter.HasHitPointsMin||filter.HasHitPointsMax||filter.HasQualityMin||filter.HasQualityMax||filter.Replace==null||filter.Replace.Selectors.Count==0||!filter.Equals(new Operations.FilterPatch{Replace=filter.Replace.Clone()}))return false;
   string? previous=null;
   foreach(var selector in filter.Replace.Selectors){
    if(selector.DefinitionCase!=Operations.FilterSelector.DefinitionOneofCase.ThingDef||!ProtoBoundary.IsIdentifier(selector.ThingDef)||!selector.Equals(new Operations.FilterSelector{ThingDef=selector.ThingDef})||previous!=null&&StringComparer.Ordinal.Compare(previous,selector.ThingDef)>=0)return false;
    previous=selector.ThingDef;
   }
   return true;
  }
 }
}
