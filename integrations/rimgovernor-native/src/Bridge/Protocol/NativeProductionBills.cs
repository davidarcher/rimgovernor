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
namespace HomeBridge.BridgeTools {
 internal static class NativeProductionBills {
  internal static string Hash(Action<BinaryWriter> write){using(var bytes=new MemoryStream()){using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true))write(writer);using(var hash=SHA256.Create())return "bill-"+BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-","").ToLowerInvariant();}}
  internal static string Configuration(Bill bill)=>Hash(w=>{
   w.Write(bill.GetUniqueLoadID());w.Write(bill.recipe.defName);w.Write(bill.suspended);w.Write(bill.ingredientSearchRadius);w.Write(bill.allowedSkillRange.min);w.Write(bill.allowedSkillRange.max);
   w.Write(bill.PawnRestriction?.GetUniqueLoadID()??"");w.Write(bill.SlavesOnly);w.Write(bill.MechsOnly);w.Write(bill.NonMechsOnly);w.Write(bill.GetStoreMode()?.defName??"");var group=bill.GetSlotGroup();w.Write(group!=null);if(group!=null){if(group.CellsList.Count>65536)throw new InvalidOperationException("Bill storage bound");w.Write(group.CellsList.Count);foreach(var c in group.CellsList.OrderBy(c=>c.x).ThenBy(c=>c.z)){w.Write(c.x);w.Write(c.z);}}
   if(bill is Bill_Production p){w.Write(p.repeatMode.defName);w.Write(p.repeatCount);w.Write(p.targetCount);w.Write(p.unpauseWhenYouHave);w.Write(p.pauseWhenSatisfied);w.Write(p.hpRange.min);w.Write(p.hpRange.max);w.Write((int)p.qualityRange.min);w.Write((int)p.qualityRange.max);w.Write(p.limitToAllowedStuff);w.Write(p.includeEquipped);w.Write(p.includeTainted);}
   w.Write(FilterConfiguration(bill.ingredientFilter));
  });
  private static string FilterConfiguration(ThingFilter filter)=>Hash(w=>{
   var defs=filter.AllowedThingDefs.OrderBy(d=>d.defName,StringComparer.Ordinal).ToArray();if(defs.Length>65536)throw new InvalidOperationException("Bill filter bound");w.Write(defs.Length);foreach(var d in defs)w.Write(d.defName);
   w.Write(filter.AllowedHitPointsPercents.min);w.Write(filter.AllowedHitPointsPercents.max);w.Write((int)filter.AllowedQualityLevels.min);w.Write((int)filter.AllowedQualityLevels.max);w.Write(filter.AllowedMentalBreakChance.min);w.Write(filter.AllowedMentalBreakChance.max);
   foreach(var f in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.OrderBy(d=>d.defName,StringComparer.Ordinal)){w.Write(f.defName);w.Write(filter.Allows(f));}
  });
  internal static Obs.SnapshotRef Snapshot(Thing bench,IBillGiver giver,Common.ObservationContext context)=>new Obs.SnapshotRef{Context=context.Clone(),EntityId=bench.GetUniqueLoadID(),Token=Hash(w=>{w.Write(context.Identity.ColonyId);w.Write(context.Identity.LoadToken);w.Write(context.Identity.MapId);w.Write(bench.GetUniqueLoadID());w.Write(giver.BillStack.Count);if(giver.BillStack.Count>15)throw new InvalidOperationException("Bill stack bound");foreach(var b in giver.BillStack.Bills){w.Write(Configuration(b));w.Write(b is Bill_Production p&&p.paused);}})};
  internal static bool Usable(Thing bench)=>bench.Spawned&&ProtoBoundary.IsLoaded(bench.Map)&&bench.Faction==Faction.OfPlayer&&!bench.IsForbidden(Faction.OfPlayer)&&!bench.Position.Fogged(bench.Map)&&!bench.IsBurning()&&bench is IBillGiver g&&g.CurrentlyUsableForBills();
  // Adding a bill is the player's act of queuing work, and the game lets a
  // player queue it on an empty fueled bench: haulers refuel the bench on
  // their own once a bill waits there. Building_WorkTable.CurrentlyUsableForBills
  // is false without fuel, so a freshly built smithy could never receive
  // its first bill (#155 M4: the production ladder stalled on the bench
  // rung's own product). Only the fuel condition is relaxed; a bench that
  // is unusable for any other reason still refuses.
  internal static bool UsableForNewBill(Thing bench)=>Usable(bench)||bench.Spawned&&ProtoBoundary.IsLoaded(bench.Map)&&bench.Faction==Faction.OfPlayer&&!bench.IsForbidden(Faction.OfPlayer)&&!bench.Position.Fogged(bench.Map)&&!bench.IsBurning()&&bench is Building_WorkTable table&&table.UsableForBillsAfterFueling();
  // Ordinary production: every product is a spawnable item (food, kibble,
  // blocks, weapons, apparel alike); corpse butchering keeps its special case.
  internal static bool Recipe(Thing bench,RecipeDef recipe)=>recipe.AvailableNow&&recipe.AvailableOnNow(bench)&&bench.def.AllRecipes.Contains(recipe)&&(recipe.defName=="ButcherCorpseFlesh"||recipe.products.Count>0&&recipe.products.All(p=>Product(p.thingDef)));
  internal static bool Product(ThingDef d)=>d!=null&&d.category==ThingCategory.Item&&!d.IsCorpse;
  internal static Obs.BillState BillRow(Bill bill,int index){
   var row=new Obs.BillState{Id=bill.GetUniqueLoadID(),Index=(uint)index,Recipe=new Obs.DefinitionRef{DefName=bill.recipe.defName},Suspended=bill.suspended,ManagedUnchanged=NativeProductionTracking.ManagedUnchanged(bill)};
   row.WorkerId=bill.PawnRestriction?.GetUniqueLoadID()??"";
   // MakeNewBill consumes a native bill id; census defaults must stay detached.
   var fresh=new Bill_Production { recipe=bill.recipe };
   fresh.ingredientFilter.CopyAllowancesFrom(bill.recipe.defaultIngredientFilter ?? bill.recipe.fixedIngredientFilter);
   if(bill is Bill_Production && bill.billStack?.billGiver is Thing bench){
    bool human=bill.recipe.defName=="ButcherCorpseFlesh" && bill.ingredientFilter.AllowedThingDefs.Any(d=>d.IsCorpse&&d.ingestible?.sourceDef?.race?.Humanlike==true);
    ConfigureIngredients(fresh,bill.recipe,new Operations.BillSettings{Worker=human?new Operations.Assignment{EntityId=row.WorkerId}:null},bench.Map);
    row.DefaultIngredients=FilterConfiguration(bill.ingredientFilter)==FilterConfiguration(fresh.ingredientFilter);
    row.UnrestrictedWorker=bill.allowedSkillRange==fresh.allowedSkillRange && bill.SlavesOnly==fresh.SlavesOnly && bill.MechsOnly==fresh.MechsOnly && bill.NonMechsOnly==fresh.NonMechsOnly;
   }
   if(bill.recipe.defName=="ButcherCorpseFlesh"){
    row.IngredientFilter=new Obs.StockpileFilter();
    row.IngredientFilter.AllowedDefNames.Add(bill.ingredientFilter.AllowedThingDefs.Where(d=>d.IsCorpse&&d.ingestible?.sourceDef?.race?.Humanlike==true).Select(d=>d.defName).OrderBy(id=>id,StringComparer.Ordinal));
   }
   else {row.IngredientFilter=new Obs.StockpileFilter();row.IngredientFilter.AllowedDefNames.Add(bill.ingredientFilter.AllowedThingDefs.Select(d=>d.defName).OrderBy(id=>id,StringComparer.Ordinal));}
   if(bill is Bill_Production p){row.RepeatMode=p.repeatMode.defName;row.RepeatCount=p.repeatCount;row.TargetCount=p.targetCount;row.UnpauseBelow=p.unpauseWhenYouHave;row.PauseWhenSatisfied=p.pauseWhenSatisfied;row.Paused=p.paused;row.Finished=BillCommon.IsFinished(p);}
   return row;
  }
  internal static Obs.RecipeState RecipeRow(Thing bench,RecipeDef recipe){var row=new Obs.RecipeState{Recipe=new Obs.DefinitionRef{DefName=recipe.defName},AvailableNow=recipe.AvailableNow,AvailableOnBench=recipe.AvailableOnNow(bench)};NativeMealRecipeFacts.Fill(row,bench.def,recipe);return row;}
  private static void ConfigureIngredients(Bill_Production bill,RecipeDef recipe,Operations.BillSettings s,Map map){
     if(s.Ingredients!=null){
      bill.ingredientFilter.SetDisallowAll();
      foreach(var selector in s.Ingredients.Replace.Selectors)bill.ingredientFilter.SetAllow(DefDatabase<ThingDef>.GetNamed(selector.ThingDef),true);
     }
     if(recipe.defName=="ButcherCorpseFlesh"){
      bool human=s.Worker!=null;
      foreach(var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d=>d.IsCorpse))
       bill.ingredientFilter.SetAllow(def,(def.ingestible?.sourceDef?.race?.Humanlike==true)==human && recipe!.ingredients.Any(i=>i.filter.Allows(def)));
     }else{
      // Shared colonist cooking never creates human-meat meals. Dedicated
      // destination bills must supply their own explicit routing contract.
      bool trade=s.Ingredients!=null&&recipe!.products.All(p=>p.thingDef.defName=="MealSurvivalPack");
      bool eligible=s.Ingredients!=null&&map.mapPawns.FreeColonistsSpawned.All(HumanFoodFacts.AcceptsMeat);
      bool feed=recipe!.products.All(p=>p.thingDef.ingestible!=null && (p.thingDef.ingestible.foodType&FoodTypeFlags.Kibble)!=0);
      foreach(var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(HumanFoodFacts.IsHumanMeat))bill.ingredientFilter.SetAllow(def,(feed || (trade || eligible) && s.Ingredients!.Replace.Selectors.Any(x=>x.ThingDef==def.defName)) && recipe.ingredients.Any(i=>i.filter.Allows(def)));
     }
  }
  internal static bool Valid(Operations.AddBill? command)=>NativeProductionBillSettings.Valid(command);
  // The refusal names the condition that failed: the production ladder's
  // bill rung reads only this message back (#155 M4 run 9 stalled on the
  // one-line summary), and each check below is a different repair.
  private static bool Prepare(Operations.AddBill command,Common.ObservationContext context,out Thing? bench,out IBillGiver? giver,out RecipeDef? recipe,out Common.Failure failure){
   bench=null;giver=null;recipe=null;
   Common.Failure Refuse(string why){return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Production bill requires unchanged native bench, available recipe and assigned skilled worker: "+why);}
   failure=Refuse("invalid request");
   if(!Valid(command)||!NativeProductionTracking.Ready)return false;
   bench=ProtoBoundary.LoadedMap(context).listerThings.AllThings.FirstOrDefault(t=>t.GetUniqueLoadID()==command.Bench.EntityId);giver=bench as IBillGiver;
   if(bench==null||giver==null){failure=Refuse("bench "+command.Bench.EntityId+" is not a loaded bill giver");return false;}
   if(!UsableForNewBill(bench)){failure=Refuse("bench is not usable for bills");return false;}
   var replaced=command.HasReplaceOwnedBillId?NativeProductionTracking.ReplaceableBill(command.ReplaceOwnedBillId,bench,command.RecipeDef):null;
   if(command.HasReplaceOwnedBillId && replaced==null){failure=Refuse("replacement must be the same recipe on this bench or an ordinary meal tier on this map");return false;}
   if(giver.BillStack.Count>=15 && replaced?.billStack!=giver.BillStack){failure=Refuse("bill stack is full");return false;}
   var humanButcher=command.RecipeDef=="ButcherCorpseFlesh"&&command.Settings.Worker!=null;
   if((replaced==null || replaced.recipe.defName!=command.RecipeDef) && giver.BillStack.Bills.Any(b=>b!=replaced && b.recipe.defName==command.RecipeDef && (command.RecipeDef!="ButcherCorpseFlesh" || b.ingredientFilter.AllowedThingDefs.Any(d=>d.IsCorpse && (d.ingestible?.sourceDef?.race?.Humanlike==true)==humanButcher)))){failure=Refuse("bench already carries a matching "+command.RecipeDef+" bill");return false;}
   recipe=DefDatabase<RecipeDef>.GetNamedSilentFail(command.RecipeDef);
   if(recipe==null||!Recipe(bench,recipe)){failure=Refuse("recipe "+command.RecipeDef+" is not available on the bench");return false;}
   if(command.RecipeDef!="ButcherCorpseFlesh"&&(recipe.WorkerCounter.GetType()!=typeof(RecipeWorkerCounter)||recipe.specialProducts!=null||recipe.products.Count!=1)){failure=Refuse("recipe "+command.RecipeDef+" is not ordinary single-product work");return false;}
   if(command.HasReplaceOwnedBillId && replaced!.recipe.defName!=command.RecipeDef && (recipe.products.Count!=1 || recipe.products[0].thingDef.ingestible==null || recipe.products[0].thingDef.ingestible.preferability<FoodPreferability.MealSimple || recipe.products[0].thingDef.ingestible.preferability>FoodPreferability.MealLavish)){failure=Refuse("replacement requires an ordinary meal recipe");return false;}
   var ingredientRecipe=recipe;
   if(command.Settings.Ingredients!=null){
    var definitions=command.Settings.Ingredients.Replace.Selectors.Select(s=>DefDatabase<ThingDef>.GetNamedSilentFail(s.ThingDef)).ToArray();
    if(definitions.Any(d=>d==null||ingredientRecipe.fixedIngredientFilter!=null&&!ingredientRecipe.fixedIngredientFilter.Allows(d)||!ingredientRecipe.ingredients.Any(i=>i.filter.Allows(d)))||ingredientRecipe.ingredients.Any(i=>!definitions.Any(d=>i.filter.Allows(d)))){failure=Refuse("ingredient filter does not fund the recipe's ingredient slots");return false;}
   }
   var target=bench;var wanted=recipe;var work=NativeBillsObservationTools.WorkType(bench.def,recipe);
   if(work==null){failure=Refuse("recipe "+command.RecipeDef+" has no work type on "+bench.def.defName);return false;}
   var colonists=ProtoBoundary.LoadedMap(context).mapPawns.FreeColonistsSpawned.Where(p=>!p.Dead&&!p.Downed&&!p.Drafted&&!p.InMentalState&&p.workSettings?.Initialized==true).ToList();
   if(humanButcher)colonists=colonists.Where(p=>p.GetUniqueLoadID()==command.Settings.Worker!.EntityId && HumanFoodFacts.AcceptsButchery(p)).ToList();
   if(colonists.Any(p=>p.workSettings.GetPriority(work)>0&&!p.WorkTypeIsDisabled(work)&&!target.IsForbidden(p)&&p.Position.DistanceTo(target.Position)<=40&&p.CanReach(target,PathEndMode.InteractionCell,Danger.None)&&(wanted.skillRequirements==null||wanted.skillRequirements.All(s=>p.skills?.GetSkill(s.skill)!=null&&!p.skills.GetSkill(s.skill).TotallyDisabled&&p.skills.GetSkill(s.skill).Level>=s.minLevel)))){
    // The bench token closes the list (#242): a moved world names the rule that moved before the hash.
    if(Snapshot(bench,giver,context).Token!=command.Bench.ExpectedSnapshotToken){failure=Refuse("bench bill stack changed since it was read");return false;}
    return true;
   }
   var assigned=colonists.Count(p=>p.workSettings.GetPriority(work)>0&&!p.WorkTypeIsDisabled(work));
   var reaching=colonists.Count(p=>!target.IsForbidden(p)&&p.Position.DistanceTo(target.Position)<=40&&p.CanReach(target,PathEndMode.InteractionCell,Danger.None));
   var skills=wanted.skillRequirements==null?"none":string.Join(",",wanted.skillRequirements.Select(s=>s.skill.defName+">="+s.minLevel));
   failure=Refuse("no free colonist works "+work.defName+" within reach of the bench with the recipe's skills (colonists "+colonists.Count+", assigned "+assigned+", reaching "+reaching+", skills "+skills+")");
   return false;
  }
  internal static Operations.PreviewReply Preview(Operations.AddBill command,Common.ObservationContext context)=>Prepare(command,context,out _,out _,out _,out var failure)?new Operations.PreviewReply{Evaluated=new Operations.PreviewEvaluation{Context=context.Clone(),Accepted=true}}:new Operations.PreviewReply{Failure=failure};
  internal static Operations.ExecuteReply Execute(NativeOperationState state,Operations.ExecuteRequest request,Common.ObservationContext context){
   NativeAttemptLedger.Admission? handle=null;Receipts.EffectEvidence? evidence=null;var pre=request.Precondition;var command=request.Operation.AddBill;
   try{
    if(!Prepare(command,context,out var bench,out var giver,out var recipe,out var failure))return new Operations.ExecuteReply{Failure=failure};
    if(!NativeControlAuthority.TryGetForGame(Current.Game,out var authority)||authority==null)return new Operations.ExecuteReply{Failure=ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired,"Native authority required.")};
    var guard=authority.Check(pre.ExpectedGeneration);context.NativeGeneration=guard.Snapshot.Generation;if(!guard.Success)return new Operations.ExecuteReply{Failure=NativeAuthorityControlTools.Refusal(guard.Error,context)};
    var admitted=state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute",request,context);if(admitted.Kind!=NativeAttemptLedger.DecisionKind.Admitted)return admitted.DecidedReply;handle=admitted.AdmittedHandle;
    using(authority.Owned()){
     if(!authority.Check(pre.ExpectedGeneration).Success||!Prepare(command,context,out bench,out giver,out recipe,out failure))throw new InvalidOperationException("Bill scope changed");
     var bill=recipe!.MakeNewBill(null) as Bill_Production;if(bill==null)throw new InvalidOperationException("Recipe is not ordinary production");
     var s=command.Settings;bill.repeatMode=s.RepeatMode==Operations.RepeatMode.Forever?BillRepeatModeDefOf.Forever:BillRepeatModeDefOf.TargetCount;
     if(s.RepeatMode==Operations.RepeatMode.Target){bill.targetCount=s.TargetCount;bill.unpauseWhenYouHave=s.UnpauseThreshold;bill.pauseWhenSatisfied=true;}
     bill.suspended=false;bill.ingredientSearchRadius=40;bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor,null);
     ConfigureIngredients(bill,recipe!,s,bench!.Map);
     if(s.Worker!=null)bill.SetPawnRestriction(ProtoBoundary.LoadedMap(context).mapPawns.FreeColonistsSpawned.Single(p=>p.GetUniqueLoadID()==s.Worker.EntityId));
     var record=new NativeProductionRecord(bench!,giver!,bill,command.Bench.ExpectedSnapshotToken);
     state.Bills.Add(pre.Attempt.Clone(),record);if(!NativeProductionTracking.Track(record))throw new InvalidOperationException("Production tracking unavailable");if(command.HasReplaceOwnedBillId){var old=NativeProductionTracking.ReplaceableBill(command.ReplaceOwnedBillId,bench!,command.RecipeDef) ?? throw new InvalidOperationException("Replaced bill lost");NativeProductionTracking.Retire(old);old.billStack.Delete(old);}
     giver!.BillStack.AddBill(bill);record.Capture();
     evidence=new Receipts.EffectEvidence{Bill=record.Evidence(context)};
    }
    return new Operations.ExecuteReply{Receipt=NativeOperationEnvelope.Applied(state.Ledger,handle,pre.Attempt,context,evidence)};
   }catch(Exception error){return handle==null?new Operations.ExecuteReply{Failure=ProtoBoundary.Fail(Common.FailureCode.NativeFailure,"Bill admission failed: "+error.GetType().Name)}:new Operations.ExecuteReply{Receipt=NativeOperationEnvelope.Uncertain(state.Ledger,handle,pre.Attempt,context,evidence,"Production needs inspection: "+error.GetType().Name)};}
  }
 }
}
