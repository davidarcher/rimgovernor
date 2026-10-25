using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using RimWorld;
using Verse;
using Verse.AI;
using Common=RimGovernor.Contracts.Wire.Common;
using Obs=RimGovernor.Contracts.Wire.Observations;
using Operations=RimGovernor.Contracts.Wire.Operations;
using Receipts=RimGovernor.Contracts.Wire.Receipts;
namespace HomeBridge.BridgeTools {
 internal static class NativeProductionBills {
  // A content digest for comparing two bills or filters (not a row token: NativeSnapshotVersions versions the bench).
  private static string Hash(Action<BinaryWriter> write){using(var bytes=new MemoryStream()){using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true))write(writer);using(var hash=SHA256.Create())return "bill-"+BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-","").ToLowerInvariant();}}
  internal static string Configuration(Bill bill,bool includeRemainingCount=true)=>Hash(raw=>{
   var w=new NativeBillConfiguration(raw);
   w.Write("socialBeer",bill is SocialBeerBill);
   w.Write("billId",bill.GetUniqueLoadID());w.Write("recipe",bill.recipe.defName);w.Write("suspended",bill.suspended);w.Write("ingredientSearchRadius",bill.ingredientSearchRadius);w.Write("skill.min",bill.allowedSkillRange.min);w.Write("skill.max",bill.allowedSkillRange.max);
   w.Write("worker",bill.PawnRestriction?.GetUniqueLoadID()??"");w.Write("slavesOnly",bill.SlavesOnly);w.Write("mechsOnly",bill.MechsOnly);w.Write("nonMechsOnly",bill.NonMechsOnly);w.Write("storeMode",bill.GetStoreMode()?.defName??"");var group=bill.GetSlotGroup();
   w.Group("storeCells",g=>{g.Write(group!=null);if(group!=null){if(group.CellsList.Count>65536)throw new InvalidOperationException("Bill storage bound");g.Write(group.CellsList.Count);foreach(var c in group.CellsList.OrderBy(c=>c.x).ThenBy(c=>c.z)){g.Write(c.x);g.Write(c.z);}}});
   if(bill is Bill_Production p){w.Write("repeatMode",p.repeatMode.defName);if(includeRemainingCount)w.Write("repeatCount",p.repeatCount);w.Write("targetCount",p.targetCount);w.Write("unpauseWhenYouHave",p.unpauseWhenYouHave);w.Write("pauseWhenSatisfied",p.pauseWhenSatisfied);w.Write("hp.min",p.hpRange.min);w.Write("hp.max",p.hpRange.max);w.Write("quality.min",(int)p.qualityRange.min);w.Write("quality.max",(int)p.qualityRange.max);w.Write("limitToAllowedStuff",p.limitToAllowedStuff);w.Write("includeEquipped",p.includeEquipped);w.Write("includeTainted",p.includeTainted);}
   raw.Write(FilterConfiguration(bill.ingredientFilter));
  });
  private static string FilterConfiguration(ThingFilter filter)=>Hash(raw=>{
   var w=new NativeBillConfiguration(raw);
   w.Group("ingredients.defs",g=>{var defs=filter.AllowedThingDefs.OrderBy(d=>d.defName,StringComparer.Ordinal).ToArray();if(defs.Length>65536)throw new InvalidOperationException("Bill filter bound");g.Write(defs.Length);foreach(var d in defs)g.Write(d.defName);});
   w.Write("ingredients.hp.min",filter.AllowedHitPointsPercents.min);w.Write("ingredients.hp.max",filter.AllowedHitPointsPercents.max);w.Write("ingredients.quality.min",(int)filter.AllowedQualityLevels.min);w.Write("ingredients.quality.max",(int)filter.AllowedQualityLevels.max);w.Write("ingredients.mentalBreak.min",filter.AllowedMentalBreakChance.min);w.Write("ingredients.mentalBreak.max",filter.AllowedMentalBreakChance.max);
   w.Group("ingredients.special",g=>{foreach(var f in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.OrderBy(d=>d.defName,StringComparer.Ordinal)){g.Write(f.defName);g.Write(filter.Allows(f));}});
  });
  internal static Obs.SnapshotRef Snapshot(Thing bench,IBillGiver giver,Common.ObservationContext context)=>new Obs.SnapshotRef{Context=context,EntityId=bench.GetUniqueLoadID(),Token=NativeSnapshotVersions.Token("bench",context.Identity,bench.GetUniqueLoadID(),w=>{w.Write(giver.BillStack.Count);if(giver.BillStack.Count>BillStack.MaxCount)throw new InvalidOperationException("Bill stack bound");foreach(var b in giver.BillStack.Bills){w.Write(Configuration(b));w.Write(b is Bill_Production p&&p.paused);}})};
  internal static bool Usable(Thing bench)=>bench.Spawned&&ProtoBoundary.IsLoaded(bench.Map)&&bench.Faction==Faction.OfPlayer&&!bench.IsForbidden(Faction.OfPlayer)&&!bench.Position.Fogged(bench.Map)&&!bench.IsBurning()&&bench is IBillGiver g&&g.CurrentlyUsableForBills();
  // Players can queue work on an empty fueled bench; haulers refuel it once a bill waits.
  // CurrentlyUsableForBills rejects an unfueled bench, so admission relaxes only that condition.
  // Every other usability check still applies.
  internal static bool UsableForNewBill(Thing bench)=>Usable(bench)||bench.Spawned&&ProtoBoundary.IsLoaded(bench.Map)&&bench.Faction==Faction.OfPlayer&&!bench.IsForbidden(Faction.OfPlayer)&&!bench.Position.Fogged(bench.Map)&&!bench.IsBurning()&&bench is Building_WorkTable table&&table.UsableForBillsAfterFueling();
  // Ordinary production: every product is a spawnable item (food, kibble,
  // blocks, weapons, apparel alike); corpse butchering keeps its special case.
  internal static bool Recipe(Thing bench,RecipeDef recipe)=>recipe.AvailableNow&&recipe.AvailableOnNow(bench)&&bench.def.AllRecipes.Contains(recipe)&&(NativeRecipeRoles.Corpse(recipe)||recipe.products.Count>0&&recipe.products.All(p=>Product(p.thingDef)));
  internal static bool CorpseRecipe(string recipe)=>NativeRecipeRoles.Corpse(NativeRecipeRoles.Named(recipe));
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
   var fresh=DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail("AllowFresh");
   if(fresh!=null)bill.ingredientFilter.SetAllow(fresh,true);
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
  // An item, or a piece of art (a sculpture): vanilla makes it packed.
  internal static bool Product(ThingDef d)=>d!=null&&(d.category==ThingCategory.Item&&!d.IsCorpse||d.category==ThingCategory.Building&&d.Minifiable&&d.HasComp(typeof(CompArt)));
  // Every bill on a workbench stack is a Bill_Production (mech and autonomous bills derive from it); another
  // class there fails the read rather than leave the bill's settings unread.
  internal static Obs.BillState BillRow(Bill bill,int index){
   if(!(bill is Bill_Production p)||!(bill.billStack?.billGiver is Thing bench))throw new InvalidOperationException("bill "+bill.GetUniqueLoadID()+" is not a production bill on a bench");
   // Census defaults stay detached from native bill ids.
   var fresh=Detached(bill.recipe);
   Operations.BillCorpseClass corpses=NativeRecipeRoles.Corpse(bill.recipe)?new Operations.BillCorpseClass.Selected(CorpseClass(bill)):new Operations.BillCorpseClass.NotApplicable();
   ConfigureIngredients(fresh,bill.recipe,null,corpses,bench.Map);
   var row=new Obs.BillState{Id=bill.GetUniqueLoadID(),Recipe=bill.recipe.defName,BillClass=bill.GetType().Name,Suspended=bill.suspended,ManagedUnchanged=NativeProductionTracking.ManagedUnchanged(bill),IngredientFilter=IngredientFilter(bill),
    Worker=bill.PawnRestriction?.GetUniqueLoadID(),
    DefaultIngredients=FilterConfiguration(bill.ingredientFilter)==FilterConfiguration(fresh.ingredientFilter),
    UnrestrictedWorker=bill.allowedSkillRange==fresh.allowedSkillRange && bill.SlavesOnly==fresh.SlavesOnly && bill.MechsOnly==fresh.MechsOnly && bill.NonMechsOnly==fresh.NonMechsOnly,
    RepeatMode=NativeEnums.Repeat(p.repeatMode),RepeatCount=p.repeatCount,TargetCount=p.targetCount,Finished=BillCommon.IsFinished(p),
    UnfinishedWork=UnfinishedWork(bill)};
   return Reservations(bill,row);
  }
  // A flesh-butchering bill lists only the humanlike corpses it admits.
  private static Obs.StockpileFilter IngredientFilter(Bill bill)=>NativeStockpileSettings.Project(bill.ingredientFilter) with { AllowedDefNames = [.. bill.ingredientFilter.AllowedThingDefs
   .Where(d=>!NativeRecipeRoles.ButcherFlesh(bill.recipe)||d.IsCorpse&&d.ingestible?.sourceDef?.race?.Humanlike==true).Select(d=>d.defName).OrderBy(id=>id,StringComparer.Ordinal)] };
  private static Obs.BillUnfinishedWork UnfinishedWork(Bill bill){
   var item=(bill as Bill_ProductionWithUft)?.BoundUft;
   if(item==null||item.Destroyed)return new Obs.BillUnfinishedWork.None();
   // Read BoundUft, not BoundWorker/BoundBill: those getters can mutate the binding.
   // Vanilla binds an item only once it has collected and consumed the whole iteration.
   if(!item.Initialized||item.Recipe!=bill.recipe||item.ingredients==null)throw new InvalidOperationException("bill "+bill.GetUniqueLoadID()+" binds unfinished item "+item.GetUniqueLoadID()+" without an initialized iteration of its recipe");
   var totals=new Dictionary<string,long>(StringComparer.Ordinal);
   foreach(var ingredient in item.ingredients){
    if(ingredient?.def==null||ingredient.Spawned||ingredient.stackCount<=0)throw new InvalidOperationException("unfinished item "+item.GetUniqueLoadID()+" holds a spawned or empty ingredient");
    totals.TryGetValue(ingredient.def.defName,out var count);
    totals[ingredient.def.defName]=checked(count+ingredient.stackCount);
   }
   return new Obs.BillUnfinishedWork.Bound{Item=item.GetUniqueLoadID(),Ingredients=[..totals.OrderBy(p=>p.Key,StringComparer.Ordinal)
    .Select(p=>new Obs.Quantity{DefName=p.Key,Units=p.Value})]};
  }

  // Each spawned pawn whose current job works this bill: the spawned things
  // it has queued (targetQueueB/countQueue) or placed.
  private static Obs.BillState Reservations(Bill bill,Obs.BillState row){
   if(!(bill.billStack?.billGiver is Thing bench)||!bench.Spawned)return row;
   foreach(var pawn in bench.Map.mapPawns.AllPawnsSpawned){
    var job=pawn.CurJob;
    if(job?.bill!=bill)continue;
    var items=new Dictionary<string,long>();
    void Add(Thing t,int n){if(t!=null&&t.Spawned&&n>0){items.TryGetValue(t.def.defName,out var c);items[t.def.defName]=c+n;}}
    if(job.targetQueueB!=null&&job.countQueue!=null)
     for(var i=0;i<Math.Min(job.targetQueueB.Count,job.countQueue.Count);i++)Add(job.targetQueueB[i].Thing,job.countQueue[i]);
    if(job.placedThings!=null)foreach(var placed in job.placedThings)Add(placed.thing,placed.Count);
    if(items.Count==0)continue;
    var reservation=new Obs.IngredientReservation{PawnId=pawn.GetUniqueLoadID()};
    foreach(var item in items.OrderBy(i=>i.Key,StringComparer.Ordinal))reservation = reservation with { Items = [.. reservation.Items, new Obs.Quantity{DefName=item.Key,Units=item.Value}] };
    row = row with { Reservations = [.. row.Reservations, reservation] };
   }
   return row;
  }
  // Only what a frame alone knows: whether the game offers the recipe now
  // and on this bench. The rest is the recipe's catalog row.
  internal static Obs.RecipeState RecipeRow(Thing bench,RecipeDef recipe)=>new Obs.RecipeState{Recipe=recipe.defName,AvailableNow=recipe.AvailableNow,AvailableOnBench=recipe.AvailableOnNow(bench)};
  internal static void ConfigureIngredients(Bill_Production bill,RecipeDef recipe,Operations.BillSettings s,Map map)=>ConfigureIngredients(bill,recipe,s.Ingredients,s.Corpses,map);
  private static void ConfigureIngredients(Bill_Production bill,RecipeDef recipe,Operations.FilterPatch? ingredients,Operations.BillCorpseClass corpses,Map map){
     if(ingredients!=null){
      bill.ingredientFilter.SetDisallowAll();
      foreach(var selector in ingredients.Replace?.Selectors ?? default)bill.ingredientFilter.SetAllow(DefDatabase<ThingDef>.GetNamed(selector.Definition?.ThingDef),true);
     }
     if(NativeRecipeRoles.Corpse(recipe)){
      if(corpses is not Operations.BillCorpseClass.Selected{Class:var of})throw new InvalidOperationException("Corpse bill requires its corpse class");
      ConfigureCorpses(bill,recipe,of);
     }
     else{
      // Shared colonist cooking never creates human-meat meals. Dedicated
      // destination bills must supply their own explicit routing contract.
      bool trade=ingredients!=null&&recipe.products.All(p=>p.thingDef==ThingDefOf.MealSurvivalPack);
      bool eligible=ingredients!=null&&map.mapPawns.FreeColonistsSpawned.All(HumanFoodFacts.AcceptsMeat);
      bool feed=recipe.products.All(p=>p.thingDef.ingestible!=null && (p.thingDef.ingestible.foodType&FoodTypeFlags.Kibble)!=0);
      foreach(var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(HumanFoodFacts.IsHumanMeat))bill.ingredientFilter.SetAllow(def,(feed || (trade || eligible) && ((ingredients ?? throw new System.InvalidOperationException("Required native value is unavailable: ingredients.")).Replace?.Selectors ?? default).Any(x=>x.Definition?.ThingDef==def.defName)) && recipe.ingredients.Any(i=>i.filter.Allows(def)));
     }
  }
  internal static bool Valid(Operations.ProductionBillIntent? intent)=>NativeProductionBillSettings.Valid(intent);
  // A detached bill carrying the recipe's default filter; MakeNewBill would consume a native bill id.
  // The parameterless Bill constructor leaves ingredientFilter null.
  private static Bill_Production Detached(RecipeDef recipe){
   var fresh=new Bill_Production { recipe=recipe, ingredientFilter=new ThingFilter() };
   fresh.ingredientFilter.CopyAllowancesFrom(recipe.defaultIngredientFilter ?? recipe.fixedIngredientFilter);
   return fresh;
  }
  // The ingredient filter the intent would give a new bill, as a configuration hash.
  internal static string ExpectedFilter(RecipeDef recipe,Operations.BillSettings settings,Map map){var fresh=Detached(recipe);ConfigureIngredients(fresh,recipe,settings,map);return FilterConfiguration(fresh.ingredientFilter);}
  // Exact spec identity: a bench may carry several bills of one recipe. An intent
  // is a resend only when recipe, ingredient filter, worker pin, repeat mode and
  // target all equal a standing, unfinished bill; any other spec places a new bill.
  internal static Bill? Standing(IBillGiver giver,Bill? except,Operations.ProductionBillIntent intent,string expectedFilter)=>giver.BillStack.Bills.FirstOrDefault(b=>b!=except && Matches(b,intent,expectedFilter));
  // A finished "do X times" bill is spent, not standing: the next batch is a new bill.
  internal static bool Matches(Bill b,Operations.ProductionBillIntent intent,string expectedFilter){
   if(!(b is Bill_Production p)||p.recipe.defName!=(intent.RecipeDef ?? "")||BillCommon.IsFinished(p))return false;
   var s=intent.Settings ?? throw new InvalidOperationException("Production bill settings missing");
   if((b is SocialBeerBill)!=s.BeerReserve)return false;
   if((p.PawnRestriction?.GetUniqueLoadID()??"")!=(s.Worker?.Value?.EntityId??""))return false;
   bool repetition=s.Repetition switch {
    Operations.BillRepetition.Forever=>p.repeatMode==BillRepeatModeDefOf.Forever,
    Operations.BillRepetition.Count{Repetitions:var count}=>p.repeatMode==BillRepeatModeDefOf.RepeatCount&&p.repeatCount==count,
    Operations.BillRepetition.Target{Quantity:var target,UnpauseThreshold:var threshold}=>p.repeatMode==BillRepeatModeDefOf.TargetCount&&p.targetCount==target&&p.unpauseWhenYouHave==threshold&&p.pauseWhenSatisfied,
    _=>throw new InvalidOperationException("Production bill repetition missing")
   };
   if(!repetition)return false;
   return FilterConfiguration(p.ingredientFilter)==expectedFilter;
  }
  internal static bool Skilled(Pawn p,Thing bench,RecipeDef recipe,WorkTypeDef work)=>p.workSettings.GetPriority(work)>0&&!p.WorkTypeIsDisabled(work)&&!bench.IsForbidden(p)&&p.Position.DistanceTo(bench.Position)<=40&&p.CanReach(bench,PathEndMode.InteractionCell,Danger.None)&&(recipe.skillRequirements==null||recipe.skillRequirements.All(s=>p.skills?.GetSkill(s.skill)!=null&&!p.skills.GetSkill(s.skill).TotallyDisabled&&p.skills.GetSkill(s.skill).Level>=s.minLevel));
  // Each reason identifies the failed condition so the production ladder can choose the
  // corresponding repair from this diagnostic.
  internal static string NoWorker(Map map,Thing bench,RecipeDef recipe,WorkTypeDef work,List<Pawn> colonists){
   var assigned=colonists.Count(p=>p.workSettings.GetPriority(work)>0&&!p.WorkTypeIsDisabled(work));
   var reaching=colonists.Count(p=>!bench.IsForbidden(p)&&p.Position.DistanceTo(bench.Position)<=40&&p.CanReach(bench,PathEndMode.InteractionCell,Danger.None));
   var skills=recipe.skillRequirements==null?"none":string.Join(",",recipe.skillRequirements.Select(s=>s.skill.defName+">="+s.minLevel));
   return "no free colonist works "+work.defName+" within reach of the bench with the recipe's skills (colonists "+colonists.Count+", assigned "+assigned+", reaching "+reaching+", skills "+skills+")";
  }
 }

 // Actions/Apply production_bill: add one bill to a player bench. A bench
 // already carrying an identical bill (recipe, ingredient filter, worker pin, repeat mode and target; no replaced bill left) applies
 // again; every other rule is checked live at apply.
 internal sealed class ProductionBillActionHandler : IActionHandler {
  internal const string Kind="Production bill";
  internal ProductionBillActionHandler(){NativeProductionTracking.Install();}
  private sealed record Target(Thing Bench, IBillGiver Giver, RecipeDef Recipe, Bill? Replaced, Bill? Standing);
  private static Common.Failure? Resolve(Operations.ProductionBillIntent? intent,Common.ObservationContext context,out Target? target){
   target=null;
   if(intent==null||!NativeProductionBills.Valid(intent))return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Production bill requires an exact bench, recipe and supported bill settings.");
   var map=ProtoBoundary.LoadedMap(context);var settings=intent.Settings ?? throw new InvalidOperationException("Production bill settings missing");
   var bench=RefIndex.Thing(map, intent.BenchId);var giver=bench as IBillGiver;
   var replaced=intent.ReplaceOwnedBill!=null&&bench!=null?NativeProductionTracking.ReplaceableBill((intent.ReplaceOwnedBill.Id ?? ""),bench,(intent.RecipeDef ?? "")):null;
   var recipe=DefDatabase<RecipeDef>.GetNamedSilentFail((intent.RecipeDef ?? ""));
   Bill? twin=null;
   if(bench!=null&&giver!=null&&recipe!=null&&NativeProductionBills.Recipe(bench,recipe)){
    string? expected=null;try{expected=NativeProductionBills.ExpectedFilter(recipe,settings,map);}catch(Exception){}
    if(expected!=null)twin=NativeProductionBills.Standing(giver,replaced,intent,expected);
   }
   if(twin!=null&&replaced==null&&bench!=null&&giver!=null&&recipe!=null){target=new Target(bench,giver,recipe,null,twin);return null;}
   var work=bench!=null&&recipe!=null?NativeBillsObservationTools.WorkType(bench.def,recipe):null;
   var colonists=map.mapPawns.FreeColonistsSpawned.Where(p=>!p.Dead&&!p.Downed&&!p.Drafted&&!p.InMentalState&&p.workSettings?.Initialized==true).ToList();
   if(settings.Worker!=null)colonists=colonists.Where(p=>p.GetUniqueLoadID()==settings.Worker.Value?.EntityId&&(!NativeRecipeRoles.ButcherFlesh(NativeRecipeRoles.Named((intent.RecipeDef ?? "")))||HumanFoodFacts.AcceptsButchery(p))).ToList();
   var rules=new ApplyPreconditions(Kind)
    .Present(()=>bench!=null&&giver!=null,"bench "+(intent.BenchId ?? "")+" is not a loaded bill giver")
    .Require(()=>NativeProductionTracking.Ready,"production tracking is unavailable")
    .Require(()=>bench != null && (NativeProductionBills.UsableForNewBill(bench)),"bench is not usable for bills")
    .Require(()=>intent.ReplaceOwnedBill==null||replaced!=null,"replacement must be the same recipe on this bench or an ordinary meal tier on this map")
    .Require(()=>giver != null && (giver.BillStack.Count<BillStack.MaxCount||replaced?.billStack==giver.BillStack),"bench_bill_slots_full: the bench already carries "+BillStack.MaxCount+" bills")
    .Require(()=>twin==null,"bench already carries an identical "+(intent.RecipeDef ?? "")+" bill")
    .Require(()=>bench != null && (recipe!=null&&NativeProductionBills.Recipe(bench,recipe)),"recipe "+(intent.RecipeDef ?? "")+" is not available on the bench")
    .Require(()=>recipe != null && (!settings.BeerReserve||recipe.products.Count==1&&recipe.products[0].thingDef==ThingDefOf.Wort),"Beer reserve requires a wort recipe")
    .Require(()=>recipe != null && (NativeProductionBills.CorpseRecipe((intent.RecipeDef ?? ""))||recipe.WorkerCounter.GetType()==typeof(RecipeWorkerCounter)&&recipe.specialProducts==null&&recipe.products.Count==1),"recipe "+(intent.RecipeDef ?? "")+" is not ordinary single-product work")
    .Require(()=>recipe != null && (replaced==null||replaced.recipe.defName==(intent.RecipeDef ?? "")||NativeProductionTracking.OrdinaryMeal(recipe)),"replacement requires an ordinary meal recipe")
    .Require(()=>recipe != null && (Funded(settings,recipe)),"ingredient filter does not fund the recipe's ingredient slots")
    .Require(()=>work!=null,"recipe "+(intent.RecipeDef ?? "")+" has no work type on "+bench?.def.defName);
   if(!rules.Holds||bench==null||giver==null||recipe==null||work==null)return rules.Failure();
   rules.Require(()=>colonists.Any(p=>NativeProductionBills.Skilled(p,bench,recipe,work)),NativeProductionBills.NoWorker(map,bench,recipe,work,colonists));
   if(!rules.Holds)return rules.Failure();
   target=new Target(bench,giver,recipe,replaced,null);
   return null;
  }
  private static bool Funded(Operations.BillSettings settings,RecipeDef recipe){
   if(settings.Ingredients==null)return true;
   var definitions=(settings.Ingredients.Replace?.Selectors ?? default).Select(s=>DefDatabase<ThingDef>.GetNamedSilentFail(s.Definition?.ThingDef)).ToArray();
   return !definitions.Any(d=>d==null||recipe.fixedIngredientFilter!=null&&!recipe.fixedIngredientFilter.Allows(d)||!recipe.ingredients.Any(i=>i.filter.Allows(d)))&&recipe.ingredients.All(i=>definitions.Any(d=>i.filter.Allows(d)));
  }
  public Common.Failure? Validate(Operations.Action action,Common.ObservationContext context){
   if(action.Intent?.ProductionBill?.Patient!=null)return NativeSurgery.Validate(action.Intent?.ProductionBill,context);
   if(action.Intent?.ProductionBill is {} mechIntent&&NativeMechBills.Handles(mechIntent))return NativeMechBills.Validate(mechIntent,context);
   var failure=Resolve(action.Intent?.ProductionBill,context,out _);
   return failure;
  }
  public Receipts.EffectEvidence Apply(Operations.Action action,Common.ObservationContext context){
   var intent=action.Intent?.ProductionBill ?? throw new InvalidOperationException("Production bill intent is missing");
   if(intent.Patient!=null)return NativeSurgery.Apply(intent,context);
   if(NativeMechBills.Handles(intent))return NativeMechBills.Apply(intent,context);
   var failure=Resolve(intent,context,out var t);
   if(failure!=null||t==null)throw new InvalidOperationException("Production bill prerequisites changed before apply: "+(failure?.Detail ?? ""));
   var standing=t.Standing;
   if(standing==null){
    var s=intent.Settings ?? throw new InvalidOperationException("Production bill settings missing");
    var bill=s.BeerReserve?new SocialBeerBill(t.Recipe):t.Recipe.MakeNewBill(null) as Bill_Production;if(bill==null)throw new InvalidOperationException("Recipe is not ordinary production");
    switch(s.Repetition){
     case Operations.BillRepetition.Forever:bill.repeatMode=BillRepeatModeDefOf.Forever;break;
     case Operations.BillRepetition.Count{Repetitions:var count}:bill.repeatMode=BillRepeatModeDefOf.RepeatCount;bill.repeatCount=count;break;
     case Operations.BillRepetition.Target{Quantity:var target,UnpauseThreshold:var threshold}:bill.repeatMode=BillRepeatModeDefOf.TargetCount;bill.targetCount=target;bill.unpauseWhenYouHave=threshold;bill.pauseWhenSatisfied=true;break;
     default:throw new InvalidOperationException("Production bill repetition missing");
    }
    bill.suspended=false;bill.ingredientSearchRadius=40;bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor,null);
    NativeProductionBills.ConfigureIngredients(bill,t.Recipe,s,t.Bench.Map);
    if(s.Worker!=null)bill.SetPawnRestriction(t.Bench.Map.mapPawns.FreeColonistsSpawned.Single(p=>p.GetUniqueLoadID()==s.Worker.Value?.EntityId));
    var record=new NativeProductionRecord(t.Giver,bill);
    if(!NativeProductionTracking.Track(record))throw new InvalidOperationException("Production tracking unavailable");
    if(t.Replaced!=null){NativeProductionTracking.Retire(t.Replaced);t.Replaced.billStack.Delete(t.Replaced);}
    t.Giver.BillStack.AddBill(bill);record.Capture();
    standing=bill;
   }
   return new Receipts.EffectEvidence{Effect=new Receipts.BillEffect{Bill=NativeRef.Of(standing.GetUniqueLoadID())}};
  }
 }
 // Actions/Apply remove_production_bill: delete one bill from a player bench.
 // Bench and bill are re-resolved live; a bill that is gone is refused and the
 // Round retries. A pawn working the bill or an unfinished item bound to it does
 // not refuse. Only ordinary production bills are removable here; medical and
 // mech (Bill_Mech) bills are not, since removing one destroys the mech forming.
 internal sealed class RemoveProductionBillActionHandler : IActionHandler {
  internal const string Kind="Remove production bill";
  private sealed record Target(Bill Bill);
  private static Common.Failure? Resolve(Operations.RemoveProductionBillIntent? intent,Common.ObservationContext context,out Target? target){
   target=null;
   if(intent==null||intent.BenchId is null||!ProtoBoundary.IsIdentifier(intent.BenchId)||intent.Bill==null||!ProtoBoundary.IsIdentifier(intent.Bill.Id))return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Remove production bill requires an exact bench and bill.");
   var map=ProtoBoundary.LoadedMap(context);
   var bench=RefIndex.Thing(map, intent.BenchId);var giver=bench as IBillGiver;
   var bill=giver?.BillStack.Bills.FirstOrDefault(b=>b.GetUniqueLoadID()==(intent.Bill.Id ?? ""));
   var rules=new ApplyPreconditions(Kind)
    .Present(()=>bench!=null&&giver!=null,"bench "+(intent.BenchId ?? "")+" is not a loaded bill giver")
    .Present(()=>bill!=null,"bill "+(intent.Bill.Id ?? "")+" is not on bench "+(intent.BenchId ?? ""))
    .Require(()=>bill is Bill_Production,"bill "+(intent.Bill.Id ?? "")+" is not an ordinary production bill");
   if(!rules.Holds)return rules.Failure();
   if(bill==null)return rules.Failure();
   target=new Target(bill);
   return null;
  }
  public Common.Failure? Validate(Operations.Action action,Common.ObservationContext context)=>Resolve(action.Intent?.RemoveProductionBill,context,out _);
  public Receipts.EffectEvidence Apply(Operations.Action action,Common.ObservationContext context){
   var intent=action.Intent?.RemoveProductionBill ?? throw new InvalidOperationException("Remove production bill intent is missing");
   var failure=Resolve(intent,context,out var t);
   if(failure!=null)throw new ApplyRefusedException(failure);
   if(t==null)throw new InvalidOperationException("The production bill is unavailable.");
   var id=t.Bill.GetUniqueLoadID();var recipe=t.Bill.recipe.defName;
   // Vanilla Delete never refuses and a pawn's job on the bill fails cleanly. The bound
   // unfinished item stays for a same-recipe bill to resume unless the caller asks for the
   // orphan to be cancelled (a recipe change): Cancel returns the ingredient share.
   if(intent.CancelUnfinished&&t.Bill is Bill_ProductionWithUft uft&&uft.BoundUft!=null&&!uft.BoundUft.Destroyed)uft.BoundUft.Destroy(DestroyMode.Cancel);
   NativeProductionTracking.Retire(t.Bill);t.Bill.billStack.Delete(t.Bill);
   return new Receipts.EffectEvidence{Effect=new Receipts.BillEffect{Bill=NativeRef.Of(id)}};
  }
 }
}
