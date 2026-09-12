#nullable enable
using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using Google.Protobuf;
using RimWorld;
using Verse;
using Verse.AI;
using Common=RimGovernor.Protocol.Common;
using Obs=RimGovernor.Protocol.Observations;
using Operations=RimGovernor.Protocol.Operations;
using Receipts=RimGovernor.Protocol.Receipts;
using Authority=RimGovernor.Protocol.Authority;
namespace HomeBridge.BridgeTools {
 internal static class NativeProductionBills {
  internal static string Hash(Action<BinaryWriter> write){using(var bytes=new MemoryStream()){using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true))write(writer);using(var hash=SHA256.Create())return "bill-"+BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-","").ToLowerInvariant();}}
  internal static string Configuration(Bill bill)=>Hash(w=>{
   w.Write(bill.GetUniqueLoadID());w.Write(bill.recipe.defName);w.Write(bill.suspended);w.Write(bill.ingredientSearchRadius);w.Write(bill.allowedSkillRange.min);w.Write(bill.allowedSkillRange.max);
   w.Write(bill.PawnRestriction?.GetUniqueLoadID()??"");w.Write(bill.SlavesOnly);w.Write(bill.MechsOnly);w.Write(bill.NonMechsOnly);w.Write(bill.GetStoreMode()?.defName??"");var group=bill.GetSlotGroup();w.Write(group!=null);if(group!=null){if(group.CellsList.Count>65536)throw new InvalidOperationException("Bill storage bound");w.Write(group.CellsList.Count);foreach(var c in group.CellsList.OrderBy(c=>c.x).ThenBy(c=>c.z)){w.Write(c.x);w.Write(c.z);}}
   if(bill is Bill_Production p){w.Write(p.repeatMode.defName);w.Write(p.repeatCount);w.Write(p.targetCount);w.Write(p.unpauseWhenYouHave);w.Write(p.pauseWhenSatisfied);w.Write(p.hpRange.min);w.Write(p.hpRange.max);w.Write((int)p.qualityRange.min);w.Write((int)p.qualityRange.max);w.Write(p.limitToAllowedStuff);w.Write(p.includeEquipped);w.Write(p.includeTainted);}
   var filter=bill.ingredientFilter;var defs=filter.AllowedThingDefs.OrderBy(d=>d.defName,StringComparer.Ordinal).ToArray();if(defs.Length>65536)throw new InvalidOperationException("Bill filter bound");w.Write(defs.Length);foreach(var d in defs)w.Write(d.defName);
   w.Write(filter.AllowedHitPointsPercents.min);w.Write(filter.AllowedHitPointsPercents.max);w.Write((int)filter.AllowedQualityLevels.min);w.Write((int)filter.AllowedQualityLevels.max);w.Write(filter.AllowedMentalBreakChance.min);w.Write(filter.AllowedMentalBreakChance.max);
   foreach(var f in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.OrderBy(d=>d.defName,StringComparer.Ordinal)){w.Write(f.defName);w.Write(filter.Allows(f));}
  });
  internal static Obs.SnapshotRef Snapshot(Thing bench,IBillGiver giver,Common.ObservationContext context)=>new Obs.SnapshotRef{Context=context.Clone(),EntityId=bench.GetUniqueLoadID(),Token=Hash(w=>{w.Write(context.Identity.ColonyId);w.Write(context.Identity.LoadToken);w.Write(context.Identity.MapId);w.Write(bench.GetUniqueLoadID());w.Write(giver.BillStack.Count);if(giver.BillStack.Count>15)throw new InvalidOperationException("Bill stack bound");foreach(var b in giver.BillStack.Bills){w.Write(Configuration(b));w.Write(b is Bill_Production p&&p.paused);}})};
  internal static bool Usable(Thing bench)=>bench.Spawned&&bench.Map==Find.CurrentMap&&bench.Faction==Faction.OfPlayer&&!bench.IsForbidden(Faction.OfPlayer)&&!bench.Position.Fogged(bench.Map)&&!bench.IsBurning()&&bench is IBillGiver g&&g.CurrentlyUsableForBills();
  internal static bool Food(ThingDef d)=>d!=null&&d.IsNutritionGivingIngestible&&!d.IsDrug&&d.ingestible!=null&&(d.ingestible.foodType&(FoodTypeFlags.Corpse|FoodTypeFlags.Kibble))==0;
  internal static bool Recipe(Thing bench,RecipeDef recipe)=>recipe.AvailableNow&&recipe.AvailableOnNow(bench)&&bench.def.AllRecipes.Contains(recipe)&&(recipe.defName=="ButcherCorpseFlesh"||recipe.products.Count>0&&recipe.products.All(p=>Food(p.thingDef)));
  internal static Obs.BillState BillRow(Bill bill,int index){
   var row=new Obs.BillState{Id=bill.GetUniqueLoadID(),Index=(uint)index,Recipe=new Obs.DefinitionRef{DefName=bill.recipe.defName},Suspended=bill.suspended};
   if(bill is Bill_Production p){row.RepeatMode=p.repeatMode.defName;row.RepeatCount=p.repeatCount;row.TargetCount=p.targetCount;row.UnpauseBelow=p.unpauseWhenYouHave;row.PauseWhenSatisfied=p.pauseWhenSatisfied;row.Paused=p.paused;row.Finished=BillCommon.IsFinished(p);}
   return row;
  }
  internal static Obs.RecipeState RecipeRow(Thing bench,RecipeDef recipe){var row=new Obs.RecipeState{Recipe=new Obs.DefinitionRef{DefName=recipe.defName},AvailableNow=recipe.AvailableNow,AvailableOnBench=recipe.AvailableOnNow(bench)};return row;}
  internal static bool Valid(Operations.AddBill? command){
   var s=command?.Settings;
   if(command?.Bench==null||!command.Bench.HasEntityId||!ProtoBoundary.IsIdentifier(command.Bench.EntityId)||!command.Bench.HasExpectedSnapshotToken||!ProtoBoundary.IsIdentifier(command.Bench.ExpectedSnapshotToken)||!command.HasRecipeDef||!ProtoBoundary.IsIdentifier(command.RecipeDef)||s==null)return false;
   var expected=new Operations.BillSettings{RepeatMode=s.RepeatMode,TargetCount=s.TargetCount,UnpauseThreshold=s.UnpauseThreshold,PauseWhenSatisfied=s.PauseWhenSatisfied,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Mode=Operations.StoreMode.DropOnFloor}};
   if(command.RecipeDef=="ButcherCorpseFlesh")return s.Equals(new Operations.BillSettings{RepeatMode=Operations.RepeatMode.Forever,Suspended=false,IngredientSearchRadius=40,Store=new Operations.BillStore{Mode=Operations.StoreMode.DropOnFloor}});
   return s.Equals(expected)&&s.RepeatMode==Operations.RepeatMode.Target&&s.HasTargetCount&&s.TargetCount>=1&&s.TargetCount<=10000&&s.HasUnpauseThreshold&&s.UnpauseThreshold==Math.Max(1,s.TargetCount/2)&&s.HasPauseWhenSatisfied&&s.PauseWhenSatisfied;
  }
  private static bool Prepare(Operations.AddBill command,Common.ObservationContext context,out Thing? bench,out IBillGiver? giver,out RecipeDef? recipe,out Common.Failure failure){
   bench=null;giver=null;recipe=null;failure=ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Production bill requires unchanged native bench, available recipe and assigned skilled worker.");
   if(!Valid(command)||!NativeProductionTracking.Ready)return false;
   bench=Find.CurrentMap.listerThings.AllThings.FirstOrDefault(t=>t.GetUniqueLoadID()==command.Bench.EntityId);giver=bench as IBillGiver;
   if(bench==null||giver==null||!Usable(bench)||giver.BillStack.Count>=15||Snapshot(bench,giver,context).Token!=command.Bench.ExpectedSnapshotToken||giver.BillStack.Bills.Any(b=>b.recipe.defName==command.RecipeDef))return false;
   recipe=DefDatabase<RecipeDef>.GetNamedSilentFail(command.RecipeDef);if(recipe==null||!Recipe(bench,recipe))return false;
   if(command.RecipeDef!="ButcherCorpseFlesh"&&(recipe.WorkerCounter.GetType()!=typeof(RecipeWorkerCounter)||recipe.specialProducts!=null||recipe.products.Count!=1))return false;
   var target=bench;var wanted=recipe;var cooking=DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
   return cooking!=null&&Find.CurrentMap.mapPawns.FreeColonistsSpawned.Any(p=>!p.Dead&&!p.Downed&&!p.Drafted&&!p.InMentalState&&p.workSettings?.Initialized==true&&p.workSettings.GetPriority(cooking)>0&&!p.WorkTypeIsDisabled(cooking)&&!target.IsForbidden(p)&&p.Position.DistanceTo(target.Position)<=40&&p.CanReach(target,PathEndMode.InteractionCell,Danger.None)&&(wanted.skillRequirements==null||wanted.skillRequirements.All(s=>p.skills?.GetSkill(s.skill)!=null&&!p.skills.GetSkill(s.skill).TotallyDisabled&&p.skills.GetSkill(s.skill).Level>=s.minLevel)));
  }
  internal static Operations.PreviewReply Preview(Operations.AddBill command,Common.ObservationContext context)=>Prepare(command,context,out _,out _,out _,out var failure)?new Operations.PreviewReply{Evaluated=new Operations.PreviewEvaluation{Context=context.Clone(),Accepted=true}}:new Operations.PreviewReply{Failure=failure};
  internal static Operations.ExecuteReply Execute(NativeOperationState state,Operations.ExecuteRequest request,Common.ObservationContext context){
   NativeAttemptLedger.Admission? handle=null;Authority.Owner? owner=null;Receipts.EffectEvidence? evidence=null;var pre=request.Precondition;var command=request.Operation.AddBill;
   try{
    if(!Prepare(command,context,out var bench,out var giver,out var recipe,out var failure))return new Operations.ExecuteReply{Failure=failure};
    if(!NativeControlAuthority.TryGetForGame(Current.Game,out var authority)||authority==null)return new Operations.ExecuteReply{Failure=ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired,"Native authority required.")};
    var guard=authority.Check(pre.ExpectedGeneration,pre.LeaseId,pre.Attempt.ControllerSessionId);context.NativeGeneration=guard.Snapshot.Generation;if(!guard.Success)return new Operations.ExecuteReply{Failure=NativeAuthorityControlTools.Refusal(guard.Error,context)};
    owner=new Authority.Owner{ControllerSessionId=guard.Snapshot.Lease!.ControllerSessionId,PlayerDirection=guard.Snapshot.Lease.PlayerDirection};var admitted=state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute",request,context,owner);if(admitted.Kind!=NativeAttemptLedger.DecisionKind.Admitted)return admitted.Reply!;handle=admitted.Handle;
    using(authority.Owned()){
     if(!authority.Check(pre.ExpectedGeneration,pre.LeaseId,pre.Attempt.ControllerSessionId).Success||!Prepare(command,context,out bench,out giver,out recipe,out failure))throw new InvalidOperationException("Bill scope changed");
     var bill=recipe!.MakeNewBill(null) as Bill_Production;if(bill==null)throw new InvalidOperationException("Recipe is not ordinary production");
     var s=command.Settings;bill.repeatMode=s.RepeatMode==Operations.RepeatMode.Forever?BillRepeatModeDefOf.Forever:BillRepeatModeDefOf.TargetCount;
     if(s.RepeatMode==Operations.RepeatMode.Target){bill.targetCount=s.TargetCount;bill.unpauseWhenYouHave=s.UnpauseThreshold;bill.pauseWhenSatisfied=true;}
     bill.suspended=false;bill.ingredientSearchRadius=40;bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor,null);
     var record=new NativeProductionRecord(bench!,giver!,bill,command.Bench.ExpectedSnapshotToken);
     state.Bills.Add(pre.Attempt.Clone(),record);if(!NativeProductionTracking.Track(record))throw new InvalidOperationException("Production tracking unavailable");giver!.BillStack.AddBill(bill);record.Capture();
     evidence=new Receipts.EffectEvidence{Bill=record.Evidence(context)};
    }
    return new Operations.ExecuteReply{Receipt=NativeOperationEnvelope.Applied(state.Ledger,handle,pre.Attempt,context,owner,evidence)};
   }catch(Exception error){return handle==null?new Operations.ExecuteReply{Failure=ProtoBoundary.Fail(Common.FailureCode.NativeFailure,"Bill admission failed: "+error.GetType().Name)}:new Operations.ExecuteReply{Receipt=NativeOperationEnvelope.Uncertain(state.Ledger,handle,pre.Attempt,context,owner!,evidence!,"Production needs inspection: "+error.GetType().Name)};}
  }
 }
}
