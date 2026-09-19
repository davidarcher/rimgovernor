#nullable enable
using System;
using Operations=RimGovernor.Protocol.Operations;
namespace HomeBridge.BridgeTools {
 internal static class NativeProductionBillSettings {
  internal static bool Valid(Operations.AddBill? command){
   var s=command?.Settings;
   if(command?.HasReplaceOwnedBillId==true && !ProtoBoundary.IsIdentifier(command.ReplaceOwnedBillId))return false;
   if(!ValidIngredients(s?.Ingredients))return false;
   if(command?.Bench==null||!command.Bench.HasEntityId||!ProtoBoundary.IsIdentifier(command.Bench.EntityId)||!command.Bench.HasExpectedSnapshotToken||!ProtoBoundary.IsIdentifier(command.Bench.ExpectedSnapshotToken)||!command.HasRecipeDef||!ProtoBoundary.IsIdentifier(command.RecipeDef)||s==null)return false;
   var expected=new Operations.BillSettings{RepeatMode=s.RepeatMode,TargetCount=s.TargetCount,UnpauseThreshold=s.UnpauseThreshold,PauseWhenSatisfied=s.PauseWhenSatisfied,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Mode=Operations.StoreMode.DropOnFloor},Ingredients=s.Ingredients?.Clone()};
   if(command.RecipeDef=="ButcherCorpseFlesh")return (s.Worker==null || s.Worker.ValueCase==Operations.Assignment.ValueOneofCase.EntityId && ProtoBoundary.IsIdentifier(s.Worker.EntityId)) && s.Equals(new Operations.BillSettings{RepeatMode=Operations.RepeatMode.Forever,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Mode=Operations.StoreMode.DropOnFloor},Worker=s.Worker?.Clone()});
   return s.Equals(expected)&&s.RepeatMode==Operations.RepeatMode.Target&&s.HasTargetCount&&s.TargetCount>=1&&s.TargetCount<=10000&&s.HasUnpauseThreshold&&s.UnpauseThreshold==Math.Max(1,s.TargetCount/2)&&s.HasPauseWhenSatisfied&&s.PauseWhenSatisfied;
  }
  internal static bool ValidIngredients(Operations.FilterPatch? filter){
   if(filter==null)return true;
   if(filter.Allow.Count!=0||filter.Disallow.Count!=0||filter.HasHitPointsMin||filter.HasHitPointsMax||filter.HasQualityMin||filter.HasQualityMax||filter.Replace==null||filter.Replace.Selectors.Count==0||filter.Replace.Selectors.Count>256||!filter.Equals(new Operations.FilterPatch{Replace=filter.Replace.Clone()}))return false;
   string? previous=null;
   foreach(var selector in filter.Replace.Selectors){
    if(selector.DefinitionCase!=Operations.FilterSelector.DefinitionOneofCase.ThingDef||!ProtoBoundary.IsIdentifier(selector.ThingDef)||!selector.Equals(new Operations.FilterSelector{ThingDef=selector.ThingDef})||previous!=null&&StringComparer.Ordinal.Compare(previous,selector.ThingDef)>=0)return false;
    previous=selector.ThingDef;
   }
   return true;
  }
 }
}
