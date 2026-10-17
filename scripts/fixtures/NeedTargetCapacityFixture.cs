#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

// Need-target capacity (#3213): one multi-meal stack shared by two hungry pawns, then a one-slot bed shared by two tired
// pawns. Only the world is staged and observed; no task, Method or order is installed.
public sealed partial class PawnControlFixture
{
    private static bool capacityPatched, capacityActive;
    private static string capacityPhase = "";
    private static Thing? capacityStack, capacityBed;
    private static int capacityObservedTick, capacityBothTiredTick;
    private static int capacityMaxStackReservers, capacityMaxBedReservers, capacityMaxBedSleepers;
    private static readonly Dictionary<string, float> capacityFoodStart = new(StringComparer.Ordinal);
    private static readonly HashSet<string> capacityAte = new(StringComparer.Ordinal);
    private static readonly HashSet<string> capacityOwnedIngest = new(StringComparer.Ordinal);
    private static readonly HashSet<string> capacityBedSlept = new(StringComparer.Ordinal);

    private static void CapacityQuiet(Map map, float food, float rest)
    {
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToArray())
        {
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            pawn.needs.food.CurLevelPercentage = food;
            pawn.needs.rest.CurLevelPercentage = rest;
            pawn.needs.joy.CurLevelPercentage = .95f;
            foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !pawn.WorkTypeIsDisabled(t))) pawn.workSettings.SetPriority(type, 0);
            foreach (var item in pawn.inventory.innerContainer.Where(t => t.def.IsNutritionGivingIngestible).ToArray()) item.Destroy();
        }
        foreach (var thing in map.listerThings.AllThings.Where(t => t.Spawned && ((t.def.IsNutritionGivingIngestible && t is not Plant) || t is Building_Bed)).ToArray()) thing.Destroy();
    }

    private static void CapacityReset(string phase)
    {
        capacityActive = false; capacityPhase = phase;
        capacityMaxStackReservers = capacityMaxBedReservers = capacityMaxBedSleepers = 0;
        capacityFoodStart.Clear(); capacityAte.Clear(); capacityOwnedIngest.Clear(); capacityBedSlept.Clear();
        capacityObservedTick = -1; capacityBothTiredTick = -1;
        if (phase == "food") capacityBed = null;
        else capacityStack = null;
        if (!capacityPatched)
        {
            var harmony = new Harmony("rimgovernor.fixture.need-capacity");
            harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(CapacityTick)));
            capacityPatched = true;
        }
    }

    [Tool("test/need_capacity_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare two hungry pawns and one four-meal simple-meal stack, with no other food or bed. Install no chosen task, Method or assignment.")]
    public async Task<object> NeedCapacityPrepare(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap;
            var pawns = map.mapPawns.FreeColonistsSpawned.ToArray();
            if (pawns.Length < 2) throw new InvalidOperationException("Need-capacity fixture needs two colonists.");
            CapacityQuiet(map, .2f, .95f);
            CapacityReset("food");
            var anchor = pawns[0].Position;
            var cell = GenRadial.RadialCellsAround(anchor, 6, false).First(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && c.DistanceTo(anchor) > 2);
            capacityStack = ThingMaker.MakeThing(ThingDefOf.MealSimple); capacityStack.stackCount = 4;
            GenSpawn.Spawn(capacityStack, cell, map);
            foreach (var pawn in pawns) capacityFoodStart[pawn.GetUniqueLoadID()] = pawn.needs.food.CurLevelPercentage;
            capacityActive = true;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

    [Tool("test/need_capacity_bed", Description="UNSAFE FOR MODEL EXECUTION. Replace the meal stack with one single-slot bed and tire both pawns. Install no chosen task, Method or assignment.")]
    public async Task<object> NeedCapacityBed(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (capacityPhase != "food" || capacityAte.Count < 2) throw new InvalidOperationException("The shared-stack phase has not completed.");
            var map = Find.CurrentMap;
            var pawns = map.mapPawns.FreeColonistsSpawned.ToArray();
            CapacityQuiet(map, .95f, .15f);
            CapacityReset("bed");
            var anchor = pawns[0].Position;
            var cell = GenRadial.RadialCellsAround(anchor, 6, false).First(c => c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && c.DistanceTo(anchor) > 2);
            capacityBed = GenSpawn.Spawn(ThingDefOf.Bed, cell, map); capacityBed.SetFaction(Faction.OfPlayer);
            capacityActive = true;
            return FixtureKit.Reply(new Fx.FixtureDone { Success = true });
        }, ct).ConfigureAwait(false);

    private static void CapacityTick(Pawn ___pawn)
    {
        if (!capacityActive || ___pawn.Map == null || !___pawn.IsColonist) return;
        var tick = Find.TickManager.TicksGame;
        var id = ___pawn.GetUniqueLoadID();
        var owned = NativePawnActivities.Read(___pawn)?.Method.StartsWith("coupled-", StringComparison.Ordinal) == true;
        if (capacityPhase == "food")
        {
            if (owned && ___pawn.CurJobDef == JobDefOf.Ingest) capacityOwnedIngest.Add(id);
            if (capacityFoodStart.TryGetValue(id, out var start) && capacityOwnedIngest.Contains(id) && ___pawn.needs.food.CurLevelPercentage > start + .3f) capacityAte.Add(id);
        }
        else if (capacityBed != null)
        {
            if (owned && ___pawn.CurJobDef == JobDefOf.LayDown && ___pawn.CurJob.targetA.Thing == capacityBed && !___pawn.Awake()) capacityBedSlept.Add(id);
            if (capacityBothTiredTick < 0 && ___pawn.Map.mapPawns.FreeColonistsSpawned.All(p => p.needs.rest.CurLevelPercentage < .3f)) capacityBothTiredTick = tick;
        }
        if (tick == capacityObservedTick) return;
        capacityObservedTick = tick;
        var map = ___pawn.Map;
        int Reservers(Thing? thing) => thing == null ? 0 : map.reservationManager.ReservationsReadOnly.Where(r => r.Target.Thing == thing).Select(r => r.Claimant).Distinct().Count();
        capacityMaxStackReservers = Math.Max(capacityMaxStackReservers, Reservers(capacityStack));
        capacityMaxBedReservers = Math.Max(capacityMaxBedReservers, Reservers(capacityBed));
        if (capacityBed != null)
            capacityMaxBedSleepers = Math.Max(capacityMaxBedSleepers, map.mapPawns.FreeColonistsSpawned.Count(p => p.CurJobDef == JobDefOf.LayDown && p.CurJob.targetA.Thing == capacityBed));
    }

    [Tool("test/need_capacity_read", Description="UNSAFE FOR MODEL EXECUTION. Observe concurrent reservations and completed ingestion/sleep from normal-planner batches; writes no orders.")]
    public async Task<object> NeedCapacityRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() => FixtureKit.Reply(new Fx.NeedCapacityRead
        {
            Tick = Find.TickManager.TicksGame, MaxStackReservers = capacityMaxStackReservers, Ate = capacityAte.Count,
            BedSlots = capacityBed is Building_Bed bed ? bed.SleepingSlotsCount : 0,
            MaxBedReservers = capacityMaxBedReservers, MaxBedSleepers = capacityMaxBedSleepers, BedSlept = capacityBedSlept.Count,
            SinceBothTired = capacityBothTiredTick < 0 ? -1 : Find.TickManager.TicksGame - capacityBothTiredTick,
        }), ct).ConfigureAwait(false);
}
