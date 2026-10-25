#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Host.Sdk;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Native;

public sealed partial class PawnControlFixture
{
    private static bool clothingPatched, clothingActive, clothingInterrupt, clothingInterrupted, clothingConserved, clothingProductObserved;
    private static string clothingMethod = "", clothingProduct = "", clothingStuff = "";
    private static string clothingInterruptedUft = "";
    private static readonly Dictionary<string, PawnActivityOrder> clothingOrders = new(StringComparer.Ordinal);
    private static int clothingBefore, clothingRequired;
    private static readonly HashSet<string> clothingReleased = new(StringComparer.Ordinal);
    private static readonly HashSet<string> clothingResumed = new(StringComparer.Ordinal);

    [Tool("test/clothing_activity_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Prepare naked wearer, tailoring bench, competing materials and split stacks; install no selected recipe, bill or assignment.")]
    public async Task<object> PrepareClothingActivity(INativeContext ctx, CancellationToken ct,
        [ToolParameter(Description = "Interrupt once after an actual unfinished garment exists", DefaultValue = false)] bool interrupt = false) =>
        await ctx.MainThread.InvokeAsync<object>(() =>
        {
            clothingActive = false;
            var map = Find.CurrentMap;
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToArray();
            var tailor = people.First(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Crafting));
            foreach (var pawn in people)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.needs.food.CurLevelPercentage = pawn.needs.rest.CurLevelPercentage = 1;
                for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !pawn.WorkTypeIsDisabled(t))) pawn.workSettings.SetPriority(type, 0);
            }
            foreach (var apparel in tailor.apparel.WornApparel.ToArray()) apparel.Destroy();
            tailor.skills.GetSkill(SkillDefOf.Crafting).Level = 20;
            foreach (var thing in map.listerThings.AllThings.Where(t => t is Apparel || t is UnfinishedThing || t.def == ThingDefOf.Cloth
                || t.def == ThingDefOf.Leather_Plain || t is Building_WorkTable).ToArray()) thing.Destroy();
            var center = GenRadial.RadialCellsAround(tailor.Position, 12, false).First(c => c.InBounds(map)
                && GenAdj.OccupiedRect(c, Rot4.North, new IntVec2(5, 5)).Cells.All(x => x.InBounds(map) && x.Standable(map) && x.GetEdifice(map) == null));
            var bench = (Building_WorkTable)GenSpawn.Spawn(DefDatabase<ThingDef>.GetNamed("HandTailoringBench"), center, map);
            bench.SetFaction(Faction.OfPlayer);
            if (interrupt)
            {
                // Scarcity is a world precondition, not a selected recipe: exactly one
                // cheapest supported garment this wearer can wear consumes this sole supply.
                var stuff = ThingDefOf.Leather_Plain;
                var quantities = bench.def.AllRecipes.Where(r => NativeActivityRecipe.TailoringRecipe(bench, r)
                    && r.products[0].thingDef.apparel.bodyPartGroups.Any(g => g == BodyPartGroupDefOf.Torso || g == BodyPartGroupDefOf.Legs)
                    && r.products[0].thingDef.apparel.developmentalStageFilter.Has(tailor.DevelopmentalStage)
                    && r.products[0].thingDef.apparel.CorrectGenderForWearing(tailor.gender)
                    && ApparelUtility.HasPartsToWear(tailor, r.products[0].thingDef)
                    && r.products[0].thingDef.stuffCategories.Any(c => stuff.stuffProps.categories.Contains(c))
                    && r.ingredients.All(i => i.filter.Allows(stuff)))
                    .Select(r => r.ingredients.Sum(i => i.CountRequiredOfFor(stuff, r, null))).Where(n => n > 1).ToArray();
                if (quantities.Length == 0) throw new InvalidOperationException("No supported finite garment input quantity for unfinished fixture.");
                var required = quantities.Min();
                for (var i = 0; i < 2; i++)
                {
                    var stack = GenSpawn.Spawn(stuff, center + new IntVec3(i - 2, 0, -3), map);
                    stack.stackCount = i == 0 ? required / 2 : required - required / 2;
                }
            }
            else
                foreach (var stuff in new[] { ThingDefOf.Cloth, ThingDefOf.Leather_Plain })
                    for (var i = 0; i < 4; i++)
                    {
                        var stack = GenSpawn.Spawn(stuff, center + new IntVec3(i - 2, 0, stuff == ThingDefOf.Cloth ? 3 : -3), map);
                        stack.stackCount = 30;
                    }            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone); zone.GetStoreSettings().filter.SetDisallowAll();
            foreach (var recipe in bench.def.AllRecipes.Where(r => r.products.Any(p => p.thingDef.IsApparel)))
                foreach (var product in recipe.products) zone.GetStoreSettings().filter.SetAllow(product.thingDef, true);
            var destination = GenRadial.RadialCellsAround(center, 7, false).First(c => c.InBounds(map) && c.Standable(map)
                && c.GetEdifice(map) == null && c.GetZone(map) == null && c.DistanceTo(center) > 5);
            zone.AddCell(destination);
            clothingMethod = clothingProduct = clothingStuff = "";
            clothingReleased.Clear(); clothingResumed.Clear();
            clothingOrders.Clear(); clothingInterruptedUft = "";
            clothingInterrupted = clothingConserved = clothingProductObserved = false;
            clothingInterrupt = interrupt;
            if (!clothingPatched)
            {
                var harmony = new Harmony("rimgovernor.fixture.clothing-activity");
                harmony.Patch(AccessTools.Method(typeof(NativePawnActivities), "Apply"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(ClothingApplied)));
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(ClothingTick)));
                clothingPatched = true;
            }
            clothingActive = true;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

    private static void ClothingApplied(PawnActivities batch)
    {
        if (!clothingActive) return;
        foreach (var order in batch.Orders)
        {
            var pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == order.Pawn);
            if (pawn == null) continue;
            var observed = NativePawnActivities.Read(pawn);
            if (order.Release)
            {
                if (clothingOrders.TryGetValue(order.Pawn, out var previous) && previous.Assignment == order.Assignment
                    && previous.Method == order.Method && previous.Revision == order.ExpectedRevision && observed == null)
                    clothingReleased.Add(order.Pawn);
                continue;
            }
            if (observed == null || observed.Assignment != order.Assignment || observed.Method != order.Method || observed.Revision != order.Revision) continue;
            var step = order.Steps.FirstOrDefault(s => s.Recipe is { Stuff.Length: > 0 });
            if (step?.Recipe is not { } choice)
            {
                if (clothingOrders.TryGetValue(order.Pawn, out var previous) && previous.Method == order.Method && previous.Assignment == order.Assignment)
                    clothingOrders[order.Pawn] = order;
                continue;
            }
            if (clothingMethod.Length != 0 && order.Method != clothingMethod && choice.Unfinished != clothingInterruptedUft) continue;
            if (clothingMethod.Length == 0)
            {
                clothingMethod = order.Method; clothingStuff = choice.Stuff;
                clothingProduct = DefDatabase<RecipeDef>.GetNamed(choice.Recipe).products.Single().thingDef.defName;
                clothingBefore = Find.CurrentMap.listerThings.AllThings.Where(t => t.def.defName == clothingStuff).Sum(t => t.stackCount);
                clothingRequired = choice.Ingredients.Sum(i => i.Count);
            }
            clothingOrders[order.Pawn] = order;
            clothingReleased.Remove(order.Pawn);
            if (choice.Unfinished.Length != 0 && choice.Unfinished == clothingInterruptedUft) clothingResumed.Add(choice.Unfinished);
        }
    }

    private static void ClothingTick(Pawn ___pawn)
    {
        if (!clothingActive || clothingMethod.Length == 0 || !clothingOrders.TryGetValue(___pawn.GetUniqueLoadID(), out var order)) return;
        var execution = NativePawnActivities.Read(___pawn);
        if (execution == null || execution.Assignment != order.Assignment || execution.Method != order.Method || execution.Revision != order.Revision) return;
        if (clothingInterrupt && !clothingInterrupted && ___pawn.CurJob?.targetB.Thing is UnfinishedThing uft)
        {
            clothingInterruptedUft = uft.GetUniqueLoadID();
            clothingInterrupted = true; ___pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false); return;
        }
        ObserveClothingOutput(___pawn.Map);
    }

    private static void ObserveClothingOutput(Map map)
    {
        if (!clothingActive || clothingMethod.Length == 0) return;
        var products = map.listerThings.AllThings.OfType<Apparel>()
            .Concat(map.mapPawns.FreeColonistsSpawned.SelectMany(p => p.apparel.WornApparel))
            .Concat(map.mapPawns.FreeColonistsSpawned.Select(p => p.carryTracker.CarriedThing).OfType<Apparel>())
            .Where(a => a.def.defName == clothingProduct && a.Stuff?.defName == clothingStuff).Distinct().Count();
        if (!clothingProductObserved && products > 0)
        {
            clothingProductObserved = true;
            var loose = map.listerThings.AllThings.Where(t => t.def.defName == clothingStuff).Sum(t => t.stackCount);
            var carried = map.mapPawns.FreeColonistsSpawned.Select(p => p.carryTracker.CarriedThing).Where(t => t?.def.defName == clothingStuff).Sum(t => t!.stackCount);
            var unfinishedInputs = map.listerThings.AllThings.OfType<UnfinishedThing>()
                .Concat(map.mapPawns.FreeColonistsSpawned.Select(p => p.carryTracker.CarriedThing).OfType<UnfinishedThing>()).Distinct()
                .SelectMany(t => t.ingredients).Where(t => t.def.defName == clothingStuff).Sum(t => t.stackCount);
            clothingConserved = loose + carried + unfinishedInputs == clothingBefore - clothingRequired && products == 1;
        }
    }

    [Tool("test/clothing_activity_read", Description = "UNSAFE FOR MODEL EXECUTION. Observe normal-planner exact garment output, input conservation and interrupted unfinished-work recovery.")]
    public async Task<object> ReadClothingActivity(INativeContext ctx, CancellationToken ct) => await ctx.MainThread.InvokeAsync<object>(() =>    {
        ObserveClothingOutput(Find.CurrentMap);
        // The ordinary ledger must never orphan the exact interrupted execution anchor.
        // Read the native forward link; BoundWorker can mutate it when priorities are zero.
        var interrupted = Find.CurrentMap.listerThings.AllThings.OfType<UnfinishedThing>()
            .FirstOrDefault(t => t.GetUniqueLoadID() == clothingInterruptedUft);
        if (interrupted != null)
        {
            var anchor = Find.CurrentMap.listerThings.AllThings.OfType<Building_WorkTable>()
                .SelectMany(b => b.BillStack.Bills).OfType<Bill_GovernorRecipe>()
                .SingleOrDefault(b => b.BoundUft == interrupted);
            if (anchor == null || anchor.ShouldDoNow() || anchor.suspended
                || NativeProductionBills.BillRow(anchor, 0).BillClass != nameof(Bill_GovernorRecipe))
                throw new InvalidOperationException("Interrupted garment lost its private nonautonomous activity bill anchor.");
        }
        return FixtureKit.Reply(new Fx.ClothingActivityRead
        {
            Tick = Find.TickManager.TicksGame, Produced = clothingProductObserved, Conserved = clothingConserved,
            Interrupted = clothingInterrupted, Resumed = clothingResumed.Count, Released = clothingReleased.Count,
        });
    }, ct).ConfigureAwait(false);
}
