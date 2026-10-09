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
  internal static Obs.SnapshotRef Snapshot(Thing bench,IBillGiver giver,Common.ObservationContext context)=>new Obs.SnapshotRef{Context=context.Clone(),EntityId=bench.GetUniqueLoadID(),Token=Hash(w=>{w.Write(context.Identity.ColonyId);w.Write(context.Identity.LoadToken);w.Write(context.Identity.MapId);w.Write(bench.GetUniqueLoadID());w.Write(giver.BillStack.Count);if(giver.BillStack.Count>BillStack.MaxCount)throw new InvalidOperationException("Bill stack bound");foreach(var b in giver.BillStack.Bills){w.Write(Configuration(b));w.Write(b is Bill_Production p&&p.paused);}})};
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
  internal static Obs.BillState BillRow(Bill bill,int index){
   var row=new Obs.BillState{Id=bill.GetUniqueLoadID(),Recipe=new Obs.DefinitionRef{DefName=bill.recipe.defName},Suspended=bill.suspended,ManagedUnchanged=NativeProductionTracking.ManagedUnchanged(bill)};
   row.Worker=NativeRef.Of(bill.PawnRestriction);
   // Census defaults stay detached from native bill ids.
   var fresh=Detached(bill.recipe);
   if(bill is Bill_Production && bill.billStack?.billGiver is Thing bench){
    var defaults=new Operations.BillSettings();
    if(NativeRecipeRoles.Corpse(bill.recipe)){defaults.CorpseClass=CorpseClass(bill);}
    ConfigureIngredients(fresh,bill.recipe,defaults,bench.Map);
    row.DefaultIngredients=FilterConfiguration(bill.ingredientFilter)==FilterConfiguration(fresh.ingredientFilter);
    row.UnrestrictedWorker=bill.allowedSkillRange==fresh.allowedSkillRange && bill.SlavesOnly==fresh.SlavesOnly && bill.MechsOnly==fresh.MechsOnly && bill.NonMechsOnly==fresh.NonMechsOnly;
   }
   if(NativeRecipeRoles.ButcherFlesh(bill.recipe)){
    row.IngredientFilter=new Obs.StockpileFilter();
    row.IngredientFilter.AllowedDefNames.Add(bill.ingredientFilter.AllowedThingDefs.Where(d=>d.IsCorpse&&d.ingestible?.sourceDef?.race?.Humanlike==true).Select(d=>d.defName).OrderBy(id=>id,StringComparer.Ordinal));
   }
   else {row.IngredientFilter=new Obs.StockpileFilter();row.IngredientFilter.AllowedDefNames.Add(bill.ingredientFilter.AllowedThingDefs.Select(d=>d.defName).OrderBy(id=>id,StringComparer.Ordinal));}
   Reservations(bill,row);
   if(bill is Bill_Production p){row.RepeatMode=NativeEnums.Repeat(p.repeatMode);row.RepeatCount=p.repeatCount;row.TargetCount=p.targetCount;row.UnpauseBelow=p.unpauseWhenYouHave;row.PauseWhenSatisfied=p.pauseWhenSatisfied;row.Paused=p.paused;row.Finished=BillCommon.IsFinished(p);}
   return row;
  }
  // Each spawned pawn whose current job works this bill: the spawned things
  // it has queued (targetQueueB/countQueue) or placed.
  private static void Reservations(Bill bill,Obs.BillState row){
   if(!(bill.billStack?.billGiver is Thing bench)||!bench.Spawned)return;
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
    foreach(var item in items.OrderBy(i=>i.Key,StringComparer.Ordinal))reservation.Items.Add(new Obs.Quantity{DefName=item.Key,Units=item.Value});
    row.Reservations.Add(reservation);
   }
  }
  internal static Obs.RecipeState RecipeRow(Thing bench,RecipeDef recipe){var row=new Obs.RecipeState{Recipe=new Obs.DefinitionRef{DefName=recipe.defName},AvailableNow=recipe.AvailableNow,AvailableOnBench=recipe.AvailableOnNow(bench)};return row;}
  internal static void ConfigureIngredients(Bill_Production bill,RecipeDef recipe,Operations.BillSettings s,Map map){
     if(s.Ingredients!=null){
      bill.ingredientFilter.SetDisallowAll();
      foreach(var selector in s.Ingredients.Replace.Selectors)bill.ingredientFilter.SetAllow(DefDatabase<ThingDef>.GetNamed(selector.ThingDef),true);
     }
     if(NativeRecipeRoles.Corpse(recipe))ConfigureCorpses(bill,recipe,s.CorpseClass);
     else{
      // Shared colonist cooking never creates human-meat meals. Dedicated
      // destination bills must supply their own explicit routing contract.
      bool trade=s.Ingredients!=null&&recipe!.products.All(p=>p.thingDef==ThingDefOf.MealSurvivalPack);
      bool eligible=s.Ingredients!=null&&map.mapPawns.FreeColonistsSpawned.All(HumanFoodFacts.AcceptsMeat);
      bool feed=recipe!.products.All(p=>p.thingDef.ingestible!=null && (p.thingDef.ingestible.foodType&FoodTypeFlags.Kibble)!=0);
      foreach(var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(HumanFoodFacts.IsHumanMeat))bill.ingredientFilter.SetAllow(def,(feed || (trade || eligible) && s.Ingredients!.Replace.Selectors.Any(x=>x.ThingDef==def.defName)) && recipe.ingredients.Any(i=>i.filter.Allows(def)));
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
   if(!(b is Bill_Production p)||p.recipe.defName!=intent.RecipeDef||BillCommon.IsFinished(p))return false;
   var s=intent.Settings;
   if((b is SocialBeerBill)!=s.BeerReserve)return false;
   if((p.PawnRestriction?.GetUniqueLoadID()??"")!=(s.Worker?.EntityId??""))return false;
   if(s.RepeatMode==Operations.RepeatMode.Forever){if(p.repeatMode!=BillRepeatModeDefOf.Forever)return false;}
   else if(s.RepeatMode==Operations.RepeatMode.Count){if(p.repeatMode!=BillRepeatModeDefOf.RepeatCount||p.repeatCount!=s.RepeatCount)return false;}
   else if(p.repeatMode!=BillRepeatModeDefOf.TargetCount||p.targetCount!=s.TargetCount||p.unpauseWhenYouHave!=s.UnpauseThreshold||!p.pauseWhenSatisfied)return false;
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
  private sealed class Target {internal Thing Bench=null!;internal IBillGiver Giver=null!;internal RecipeDef Recipe=null!;internal Bill? Replaced;internal Bill? Standing;}
  private static Common.Failure? Resolve(Operations.ProductionBillIntent? intent,Common.ObservationContext context,out Target target){
   target=new Target();
   if(!NativeProductionBills.Valid(intent))return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Production bill requires an exact bench, recipe and supported bill settings.");
   var map=ProtoBoundary.LoadedMap(context);var t=target;
   var bench=map.listerThings.AllThings.FirstOrDefault(x=>x.GetUniqueLoadID()==intent!.BenchId);var giver=bench as IBillGiver;
   var replaced=intent!.ReplaceOwnedBill!=null&&bench!=null?NativeProductionTracking.ReplaceableBill(intent.ReplaceOwnedBill.Id,bench,intent.RecipeDef):null;
   var recipe=DefDatabase<RecipeDef>.GetNamedSilentFail(intent.RecipeDef);
   Bill? twin=null;
   if(giver!=null&&recipe!=null&&NativeProductionBills.Recipe(bench!,recipe)){
    string? expected=null;try{expected=NativeProductionBills.ExpectedFilter(recipe,intent.Settings,map);}catch(Exception){}
    if(expected!=null)twin=NativeProductionBills.Standing(giver,replaced,intent,expected);
   }
   if(twin!=null&&replaced==null){t.Bench=bench!;t.Giver=giver!;t.Standing=twin;return null;}
   var work=bench!=null&&recipe!=null?NativeBillsObservationTools.WorkType(bench.def,recipe):null;
   var colonists=map.mapPawns.FreeColonistsSpawned.Where(p=>!p.Dead&&!p.Downed&&!p.Drafted&&!p.InMentalState&&p.workSettings?.Initialized==true).ToList();
   if(intent.Settings.Worker!=null)colonists=colonists.Where(p=>p.GetUniqueLoadID()==intent.Settings.Worker.EntityId&&(!NativeRecipeRoles.ButcherFlesh(NativeRecipeRoles.Named(intent.RecipeDef))||HumanFoodFacts.AcceptsButchery(p))).ToList();
   var rules=new ApplyPreconditions(Kind)
    .Present(()=>bench!=null&&giver!=null,"bench "+intent.BenchId+" is not a loaded bill giver")
    .Require(()=>NativeProductionTracking.Ready,"production tracking is unavailable")
    .Require(()=>NativeProductionBills.UsableForNewBill(bench!),"bench is not usable for bills")
    .Require(()=>intent.ReplaceOwnedBill==null||replaced!=null,"replacement must be the same recipe on this bench or an ordinary meal tier on this map")
    .Require(()=>giver!.BillStack.Count<BillStack.MaxCount||replaced?.billStack==giver.BillStack,"bench_bill_slots_full: the bench already carries "+BillStack.MaxCount+" bills")
    .Require(()=>twin==null,"bench already carries an identical "+intent.RecipeDef+" bill")
    .Require(()=>recipe!=null&&NativeProductionBills.Recipe(bench!,recipe),"recipe "+intent.RecipeDef+" is not available on the bench")
    .Require(()=>!intent.Settings.BeerReserve||recipe!.products.Count==1&&recipe.products[0].thingDef==ThingDefOf.Wort,"Beer reserve requires a wort recipe")
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
  public Common.Failure? Validate(Operations.Action action,Common.ObservationContext context){
   if(action.ProductionBill?.Patient!=null)return NativeSurgery.Validate(action.ProductionBill,context);
   if(NativeMechBills.Handles(action.ProductionBill))return NativeMechBills.Validate(action.ProductionBill!,context);
   var failure=Resolve(action.ProductionBill,context,out _);
   return failure;
  }
  public Receipts.EffectEvidence Apply(Operations.Action action,Common.ObservationContext context){
   var intent=action.ProductionBill;
   if(intent.Patient!=null)return NativeSurgery.Apply(intent,context);
   if(NativeMechBills.Handles(intent))return NativeMechBills.Apply(intent,context);
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
   return new Receipts.EffectEvidence{Bill=new Receipts.BillEffect{Stack=new Receipts.SnapshotEvidence{EntityId=t.Bench.GetUniqueLoadID()},Bill=NativeRef.Of(standing.GetUniqueLoadID()),RecipeDef=standing.recipe.defName}};
  }
 }
 // Actions/Apply remove_production_bill: delete one bill from a player bench.
 // Bench and bill are re-resolved live; a bill that is gone is refused and the
 // Round retries. A pawn working the bill or an unfinished item bound to it does
 // not refuse. Only ordinary production bills are removable here; medical and
 // mech (Bill_Mech) bills are not, since removing one destroys the mech forming.
 internal sealed class RemoveProductionBillActionHandler : IActionHandler {
  internal const string Kind="Remove production bill";
  private sealed class Target {internal Thing Bench=null!;internal Bill Bill=null!;}
  private static Common.Failure? Resolve(Operations.RemoveProductionBillIntent? intent,Common.ObservationContext context,out Target target){
   target=new Target();
   if(intent==null||!intent.HasBenchId||!ProtoBoundary.IsIdentifier(intent.BenchId)||intent.Bill==null||!ProtoBoundary.IsIdentifier(intent.Bill.Id))return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,"Remove production bill requires an exact bench and bill.");
   var map=ProtoBoundary.LoadedMap(context);var t=target;
   var bench=map.listerThings.AllThings.FirstOrDefault(x=>x.GetUniqueLoadID()==intent.BenchId);var giver=bench as IBillGiver;
   var bill=giver?.BillStack.Bills.FirstOrDefault(b=>b.GetUniqueLoadID()==intent.Bill.Id);
   var rules=new ApplyPreconditions(Kind)
    .Present(()=>bench!=null&&giver!=null,"bench "+intent.BenchId+" is not a loaded bill giver")
    .Present(()=>bill!=null,"bill "+intent.Bill.Id+" is not on bench "+intent.BenchId)
    .Require(()=>bill is Bill_Production,"bill "+intent.Bill.Id+" is not an ordinary production bill");
   if(!rules.Holds)return rules.Failure();
   t.Bench=bench!;t.Bill=bill!;
   return null;
  }
  public Common.Failure? Validate(Operations.Action action,Common.ObservationContext context)=>Resolve(action.RemoveProductionBill,context,out _);
  public Receipts.EffectEvidence Apply(Operations.Action action,Common.ObservationContext context){
   var intent=action.RemoveProductionBill;
   var failure=Resolve(intent,context,out var t);
   if(failure!=null)throw new ApplyRefusedException(failure);
   var id=t.Bill.GetUniqueLoadID();var recipe=t.Bill.recipe.defName;
   // Vanilla Delete never refuses and a pawn's job on the bill fails cleanly. The bound
   // unfinished item stays for a same-recipe bill to resume unless the caller asks for the
   // orphan to be cancelled (a recipe change): Cancel returns the ingredient share.
   if(intent.CancelUnfinished&&t.Bill is Bill_ProductionWithUft uft&&uft.BoundUft!=null&&!uft.BoundUft.Destroyed)uft.BoundUft.Destroy(DestroyMode.Cancel);
   NativeProductionTracking.Retire(t.Bill);t.Bill.billStack.Delete(t.Bill);
   return new Receipts.EffectEvidence{Bill=new Receipts.BillEffect{Stack=new Receipts.SnapshotEvidence{EntityId=t.Bench.GetUniqueLoadID()},Bill=NativeRef.Of(id),RecipeDef=recipe}};
  }
 }
}
