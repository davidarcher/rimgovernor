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
  internal static string Configuration(Bill bill)=>Hash(raw=>{
   var w=new NativeBillConfiguration(raw);
   w.Write("socialBeer",bill is SocialBeerBill);
   w.Write("billId",bill.GetUniqueLoadID());w.Write("recipe",bill.recipe.defName);w.Write("suspended",bill.suspended);w.Write("ingredientSearchRadius",bill.ingredientSearchRadius);w.Write("skill.min",bill.allowedSkillRange.min);w.Write("skill.max",bill.allowedSkillRange.max);
   w.Write("worker",bill.PawnRestriction?.GetUniqueLoadID()??"");w.Write("slavesOnly",bill.SlavesOnly);w.Write("mechsOnly",bill.MechsOnly);w.Write("nonMechsOnly",bill.NonMechsOnly);w.Write("storeMode",bill.GetStoreMode()?.defName??"");var group=bill.GetSlotGroup();
   w.Group("storeCells",g=>{g.Write(group!=null);if(group!=null){if(group.CellsList.Count>65536)throw new InvalidOperationException("Bill storage bound");g.Write(group.CellsList.Count);foreach(var c in group.CellsList.OrderBy(c=>c.x).ThenBy(c=>c.z)){g.Write(c.x);g.Write(c.z);}}});
   if(bill is Bill_Production p){w.Write("repeatMode",p.repeatMode.defName);w.Write("repeatCount",p.repeatCount);w.Write("targetCount",p.targetCount);w.Write("unpauseWhenYouHave",p.unpauseWhenYouHave);w.Write("pauseWhenSatisfied",p.pauseWhenSatisfied);w.Write("hp.min",p.hpRange.min);w.Write("hp.max",p.hpRange.max);w.Write("quality.min",(int)p.qualityRange.min);w.Write("quality.max",(int)p.qualityRange.max);w.Write("limitToAllowedStuff",p.limitToAllowedStuff);w.Write("includeEquipped",p.includeEquipped);w.Write("includeTainted",p.includeTainted);}
   raw.Write(FilterConfiguration(bill.ingredientFilter));
  });
  private static string FilterConfiguration(ThingFilter filter)=>Hash(raw=>{
   var w=new NativeBillConfiguration(raw);
   w.Group("ingredients.defs",g=>{var defs=filter.AllowedThingDefs.OrderBy(d=>d.defName,StringComparer.Ordinal).ToArray();if(defs.Length>65536)throw new InvalidOperationException("Bill filter bound");g.Write(defs.Length);foreach(var d in defs)g.Write(d.defName);});
   w.Write("ingredients.hp.min",filter.AllowedHitPointsPercents.min);w.Write("ingredients.hp.max",filter.AllowedHitPointsPercents.max);w.Write("ingredients.quality.min",(int)filter.AllowedQualityLevels.min);w.Write("ingredients.quality.max",(int)filter.AllowedQualityLevels.max);w.Write("ingredients.mentalBreak.min",filter.AllowedMentalBreakChance.min);w.Write("ingredients.mentalBreak.max",filter.AllowedMentalBreakChance.max);
   w.Group("ingredients.special",g=>{foreach(var f in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.OrderBy(d=>d.defName,StringComparer.Ordinal)){g.Write(f.defName);g.Write(filter.Allows(f));}});
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
  internal static bool Recipe(Thing bench,RecipeDef recipe)=>recipe.AvailableNow&&recipe.AvailableOnNow(bench)&&bench.def.AllRecipes.Contains(recipe)&&(CorpseRecipe(recipe.defName)||recipe.products.Count>0&&recipe.products.All(p=>Product(p.thingDef)));
  internal static bool CorpseRecipe(string recipe)=>NativeProductionBillSettings.CorpseRecipe(recipe);
  private static bool HumanlikeCorpse(ThingDef d)=>d.IsCorpse&&d.ingestible?.sourceDef?.race?.Humanlike==true;
  // The class a corpse bill takes: animal without humanlike corpses, else
  // stranger when the stranger special filter is allowed, else colonist.
  internal static Common.CorpseClass CorpseClass(Bill bill){
   if(!bill.ingredientFilter.AllowedThingDefs.Any(HumanlikeCorpse))return Common.CorpseClass.Animal;
   var stranger=DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail("AllowCorpsesStranger");
   return stranger!=null&&bill.ingredientFilter.Allows(stranger)?Common.CorpseClass.Stranger:Common.CorpseClass.Colonist;
  }
  // The one corpse filter path: corpse defs by humanlike status within the
  // recipe's own filters, then the CorpsesHumanlike special filters.
  private static void ConfigureCorpses(Bill_Production bill,RecipeDef recipe,Common.CorpseClass of){
   bool human=of!=Common.CorpseClass.Animal;
   foreach(var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d=>d.IsCorpse))
    bill.ingredientFilter.SetAllow(def,HumanlikeCorpse(def)==human&&(recipe.fixedIngredientFilter==null||recipe.fixedIngredientFilter.Allows(def))&&recipe.ingredients.Any(i=>i.filter.Allows(def)));
   if(!human)return;
   // Colonist covers the colony's slaves; everything else under the
   // humanlike corpse category (unnatural, future DLC rows) is refused.
   var allowed=of==Common.CorpseClass.Colonist?new[]{"AllowCorpsesColonist","AllowCorpsesSlave"}:new[]{"AllowCorpsesStranger"};
   foreach(var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(f=>f.parentCategory?.defName=="CorpsesHumanlike"))
    bill.ingredientFilter.SetAllow(special,allowed.Contains(special.defName));
  }
  internal static bool Product(ThingDef d)=>d!=null&&d.category==ThingCategory.Item&&!d.IsCorpse;
  internal static Obs.BillState BillRow(Bill bill,int index){
   var row=new Obs.BillState{Id=bill.GetUniqueLoadID(),Index=(uint)index,Recipe=new Obs.DefinitionRef{DefName=bill.recipe.defName},Suspended=bill.suspended,ManagedUnchanged=NativeProductionTracking.ManagedUnchanged(bill)};
   row.WorkerId=bill.PawnRestriction?.GetUniqueLoadID()??"";
   // MakeNewBill consumes a native bill id; census defaults must stay detached.
   // The parameterless Bill constructor leaves ingredientFilter null.
   var fresh=new Bill_Production { recipe=bill.recipe, ingredientFilter=new ThingFilter() };
   fresh.ingredientFilter.CopyAllowancesFrom(bill.recipe.defaultIngredientFilter ?? bill.recipe.fixedIngredientFilter);
   if(bill is Bill_Production && bill.billStack?.billGiver is Thing bench){
    var defaults=new Operations.BillSettings();
    if(CorpseRecipe(bill.recipe.defName))defaults.CorpseClass=CorpseClass(bill);
    ConfigureIngredients(fresh,bill.recipe,defaults,bench.Map);
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
  internal static void ConfigureIngredients(Bill_Production bill,RecipeDef recipe,Operations.BillSettings s,Map map){
     if(s.Ingredients!=null){
      bill.ingredientFilter.SetDisallowAll();
      foreach(var selector in s.Ingredients.Replace.Selectors)bill.ingredientFilter.SetAllow(DefDatabase<ThingDef>.GetNamed(selector.ThingDef),true);
     }
     if(CorpseRecipe(recipe.defName))ConfigureCorpses(bill,recipe,s.CorpseClass);
     else{
      // Shared colonist cooking never creates human-meat meals. Dedicated
      // destination bills must supply their own explicit routing contract.
      bool trade=s.Ingredients!=null&&recipe!.products.All(p=>p.thingDef.defName=="MealSurvivalPack");
      bool eligible=s.Ingredients!=null&&map.mapPawns.FreeColonistsSpawned.All(HumanFoodFacts.AcceptsMeat);
      bool feed=recipe!.products.All(p=>p.thingDef.ingestible!=null && (p.thingDef.ingestible.foodType&FoodTypeFlags.Kibble)!=0);
      foreach(var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(HumanFoodFacts.IsHumanMeat))bill.ingredientFilter.SetAllow(def,(feed || (trade || eligible) && s.Ingredients!.Replace.Selectors.Any(x=>x.ThingDef==def.defName)) && recipe.ingredients.Any(i=>i.filter.Allows(def)));
     }
  }
  internal static bool Valid(Operations.ProductionBillIntent? intent)=>NativeProductionBillSettings.Valid(intent);
  // A bench carries at most one bill per recipe (and corpse class, and
  // pinned worker when the intent pins one, #1190): the one a resent or
  // replanned intent finds standing.
  internal static bool Matching(IBillGiver giver,Bill? except,Operations.ProductionBillIntent intent)=>giver.BillStack.Bills.Any(b=>b!=except && Matches(b,intent));
  internal static bool Matches(Bill b,Operations.ProductionBillIntent intent)=>b.recipe.defName==intent.RecipeDef && (!CorpseRecipe(intent.RecipeDef) || CorpseClass(b)==intent.Settings.CorpseClass) && (intent.Settings.Worker==null || b.PawnRestriction?.GetUniqueLoadID()==intent.Settings.Worker.EntityId);
  internal static bool Skilled(Pawn p,Thing bench,RecipeDef recipe,WorkTypeDef work)=>p.workSettings.GetPriority(work)>0&&!p.WorkTypeIsDisabled(work)&&!bench.IsForbidden(p)&&p.Position.DistanceTo(bench.Position)<=40&&p.CanReach(bench,PathEndMode.InteractionCell,Danger.None)&&(recipe.skillRequirements==null||recipe.skillRequirements.All(s=>p.skills?.GetSkill(s.skill)!=null&&!p.skills.GetSkill(s.skill).TotallyDisabled&&p.skills.GetSkill(s.skill).Level>=s.minLevel));
  // Each reason names the condition that failed: the production ladder's
  // bill rung reads only this message back (#155 M4 run 9 stalled on the
  // one-line summary), and each check below is a different repair.
  internal static string NoWorker(Map map,Thing bench,RecipeDef recipe,WorkTypeDef work,List<Pawn> colonists){
   var assigned=colonists.Count(p=>p.workSettings.GetPriority(work)>0&&!p.WorkTypeIsDisabled(work));
   var reaching=colonists.Count(p=>!bench.IsForbidden(p)&&p.Position.DistanceTo(bench.Position)<=40&&p.CanReach(bench,PathEndMode.InteractionCell,Danger.None));
   var skills=recipe.skillRequirements==null?"none":string.Join(",",recipe.skillRequirements.Select(s=>s.skill.defName+">="+s.minLevel));
   return "no free colonist works "+work.defName+" within reach of the bench with the recipe's skills (colonists "+colonists.Count+", assigned "+assigned+", reaching "+reaching+", skills "+skills+")";
  }
 }

 // Actions/Apply production_bill: add one bill to a player bench. A bench
 // already carrying a matching bill (and no replaced bill left) applies
 // again; every other rule is checked live at apply.
 internal sealed class ProductionBillActionHandler : IActionHandler {
  internal const string Kind="Production bill";
  internal ProductionBillActionHandler(){NativeProductionTracking.Install();}
  private sealed class Target {internal Thing Bench=null!;internal IBillGiver Giver=null!;internal RecipeDef Recipe=null!;internal Bill? Replaced;internal Bill? Standing;}
  private static Common.Failure? Resolve(Operations.ProductionBillIntent? intent,Common.ObservationContext context,out Target target){
   target=new Target();
   if(!NativeProductionBills.Valid(intent))return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Production bill requires an exact bench, recipe and supported bill settings.");
   var map=ProtoBoundary.LoadedMap(context);var t=target;
   var bench=map.listerThings.AllThings.FirstOrDefault(x=>x.GetUniqueLoadID()==intent!.BenchId);var giver=bench as IBillGiver;
   var replaced=intent!.HasReplaceOwnedBillId&&bench!=null?NativeProductionTracking.ReplaceableBill(intent.ReplaceOwnedBillId,bench,intent.RecipeDef):null;
   if(giver!=null&&replaced==null&&NativeProductionBills.Matching(giver,null,intent)){t.Bench=bench!;t.Giver=giver;t.Standing=giver.BillStack.Bills.First(b=>NativeProductionBills.Matches(b,intent));return null;}
   var recipe=DefDatabase<RecipeDef>.GetNamedSilentFail(intent.RecipeDef);
   var work=bench!=null&&recipe!=null?NativeBillsObservationTools.WorkType(bench.def,recipe):null;
   var colonists=map.mapPawns.FreeColonistsSpawned.Where(p=>!p.Dead&&!p.Downed&&!p.Drafted&&!p.InMentalState&&p.workSettings?.Initialized==true).ToList();
   if(intent.Settings.Worker!=null)colonists=colonists.Where(p=>p.GetUniqueLoadID()==intent.Settings.Worker.EntityId&&(intent.RecipeDef!="ButcherCorpseFlesh"||HumanFoodFacts.AcceptsButchery(p))).ToList();
   var rules=new ApplyPreconditions(Kind)
    .Present(()=>bench!=null&&giver!=null,"bench "+intent.BenchId+" is not a loaded bill giver")
    .Require(()=>NativeProductionTracking.Ready,"production tracking is unavailable")
    .Require(()=>NativeProductionBills.UsableForNewBill(bench!),"bench is not usable for bills")
    .Require(()=>!intent.HasReplaceOwnedBillId||replaced!=null,"replacement must be the same recipe on this bench or an ordinary meal tier on this map")
    .Require(()=>giver!.BillStack.Count<15||replaced?.billStack==giver.BillStack,"bill stack is full")
    .Require(()=>replaced!=null&&replaced.recipe.defName==intent.RecipeDef||!NativeProductionBills.Matching(giver!,replaced,intent),"bench already carries a matching "+intent.RecipeDef+" bill")
    .Require(()=>recipe!=null&&NativeProductionBills.Recipe(bench!,recipe),"recipe "+intent.RecipeDef+" is not available on the bench")
    .Require(()=>!intent.Settings.BeerReserve||recipe!.products.Count==1&&recipe.products[0].thingDef.defName=="Wort","Beer reserve requires a wort recipe")
    .Require(()=>NativeProductionBills.CorpseRecipe(intent.RecipeDef)||recipe!.WorkerCounter.GetType()==typeof(RecipeWorkerCounter)&&recipe.specialProducts==null&&recipe.products.Count==1,"recipe "+intent.RecipeDef+" is not ordinary single-product work")
    .Require(()=>replaced==null||replaced.recipe.defName==intent.RecipeDef||NativeProductionTracking.OrdinaryMeal(recipe!),"replacement requires an ordinary meal recipe")
    .Require(()=>Funded(intent,recipe!),"ingredient filter does not fund the recipe's ingredient slots")
    .Require(()=>work!=null,"recipe "+intent.RecipeDef+" has no work type on "+bench?.def.defName);
   if(rules.Holds)rules.Require(()=>colonists.Any(p=>NativeProductionBills.Skilled(p,bench!,recipe!,work!)),NativeProductionBills.NoWorker(map,bench!,recipe!,work!,colonists));
   if(!rules.Holds)return rules.Failure();
   t.Bench=bench!;t.Giver=giver!;t.Recipe=recipe!;t.Replaced=replaced;
   return null;
  }
  private static bool Funded(Operations.ProductionBillIntent intent,RecipeDef recipe){
   if(intent.Settings.Ingredients==null)return true;
   var definitions=intent.Settings.Ingredients.Replace.Selectors.Select(s=>DefDatabase<ThingDef>.GetNamedSilentFail(s.ThingDef)).ToArray();
   return !definitions.Any(d=>d==null||recipe.fixedIngredientFilter!=null&&!recipe.fixedIngredientFilter.Allows(d)||!recipe.ingredients.Any(i=>i.filter.Allows(d)))&&recipe.ingredients.All(i=>definitions.Any(d=>i.filter.Allows(d)));
  }
  public Common.Failure? Validate(Operations.Action action,Common.ObservationContext context)=>Resolve(action.ProductionBill,context,out _);
  public Receipts.EffectEvidence Apply(Operations.Action action,Common.ObservationContext context){
   var intent=action.ProductionBill;
   var failure=Resolve(intent,context,out var t);
   if(failure!=null)throw new InvalidOperationException("Production bill prerequisites changed before apply: "+failure.Detail);
   var standing=t.Standing;
   if(standing==null){
    var s=intent.Settings;
    var bill=s.BeerReserve?new SocialBeerBill(t.Recipe):t.Recipe.MakeNewBill(null) as Bill_Production;if(bill==null)throw new InvalidOperationException("Recipe is not ordinary production");
    if(s.RepeatMode==Operations.RepeatMode.Forever)bill.repeatMode=BillRepeatModeDefOf.Forever;
    else if(s.RepeatMode==Operations.RepeatMode.Count){bill.repeatMode=BillRepeatModeDefOf.RepeatCount;bill.repeatCount=s.RepeatCount;}
    else{bill.repeatMode=BillRepeatModeDefOf.TargetCount;bill.targetCount=s.TargetCount;bill.unpauseWhenYouHave=s.UnpauseThreshold;bill.pauseWhenSatisfied=true;}
    bill.suspended=false;bill.ingredientSearchRadius=40;bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor,null);
    NativeProductionBills.ConfigureIngredients(bill,t.Recipe,s,t.Bench.Map);
    if(s.Worker!=null)bill.SetPawnRestriction(t.Bench.Map.mapPawns.FreeColonistsSpawned.Single(p=>p.GetUniqueLoadID()==s.Worker.EntityId));
    var record=new NativeProductionRecord(t.Giver,bill);
    if(!NativeProductionTracking.Track(record))throw new InvalidOperationException("Production tracking unavailable");
    if(t.Replaced!=null){NativeProductionTracking.Retire(t.Replaced);t.Replaced.billStack.Delete(t.Replaced);}
    t.Giver.BillStack.AddBill(bill);record.Capture();
    standing=bill;
   }
   return new Receipts.EffectEvidence{Bill=new Receipts.BillEffect{Stack=new Receipts.SnapshotEvidence{EntityId=t.Bench.GetUniqueLoadID()},BillId=standing.GetUniqueLoadID(),RecipeDef=standing.recipe.defName}};
  }
 }
}
