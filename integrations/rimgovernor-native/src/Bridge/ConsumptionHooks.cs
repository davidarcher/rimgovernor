#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Realized consumption (#2441): what the colony's stock was spent on, counted
    // into the saved ConsumptionState. Hybrid: per-reason hooks count and set a
    // [ThreadStatic] context; a Thing.Destroy fallback counts only when no
    // context is set (destroyed_other). A hook that misses a path under-counts,
    // which only lowers a rate; nothing here gates anything.
    //
    // Context: 0 none, Suppress (-1) a hook already counted this call and the
    // destroys inside it are not counted again, a positive value attributes the
    // destroys inside the call to that reason.
    internal static class ConsumptionHooks
    {
        private const int Suppress = -1;
        [ThreadStatic] private static int context;
        // A shell hauled to a mortar is destroyed right after LoadShell counted
        // it: swallow that one destroy (same def, same tick).
        [ThreadStatic] private static ThingDef? swallowDef;
        [ThreadStatic] private static int swallowTick;
        private static bool patched;

        internal interface ITag { int Reason { get; } }
        internal struct BillTag : ITag { public int Reason => (int)ConsumptionReason.BillIngredient; }
        internal struct PasteTag : ITag { public int Reason => (int)ConsumptionReason.NutrientPaste; }
        internal struct FuelTag : ITag { public int Reason => (int)ConsumptionReason.FuelLoaded; }
        internal struct RotTag : ITag { public int Reason => (int)ConsumptionReason.Rot; }

        // Enter/Exit pair that attributes the destroys inside one patched call.
        internal static class Scope<T> where T : struct, ITag
        {
            public static void Enter(out int __state) { __state = context; context = default(T).Reason; }
            public static void Exit(int __state) { context = __state; }
        }

        internal static void Install()
        {
            if (patched) return;
            patched = true;
            var harmony = new Harmony("rimgovernor.consumption");
            void Pair(System.Reflection.MethodBase? target, Type tag)
            {
                if (target == null) { Log.Warning("[RimGovernor] consumption hook target missing; that reason under-counts."); return; }
                var scope = typeof(Scope<>).MakeGenericType(tag);
                harmony.Patch(target, prefix: new HarmonyMethod(scope.GetMethod("Enter")), finalizer: new HarmonyMethod(scope.GetMethod("Exit")));
            }
            void Guarded(System.Reflection.MethodBase? target, string? prefix, string? finalizer)
            {
                if (target == null) { Log.Warning("[RimGovernor] consumption hook target missing; that reason under-counts."); return; }
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(ConsumptionHooks), prefix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(ConsumptionHooks), finalizer));
            }
            harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Destroy)), prefix: new HarmonyMethod(typeof(ConsumptionHooks), nameof(BeforeDestroy)));
            Guarded(AccessTools.Method(typeof(Thing), nameof(Thing.Kill)), nameof(BeforeKill), nameof(Restore));
            Guarded(AccessTools.Method(typeof(Thing), nameof(Thing.SplitOff)), nameof(BeforeSplit), null);
            Pair(AccessTools.Method(typeof(RecipeWorker), nameof(RecipeWorker.ConsumeIngredient)), typeof(BillTag));
            Guarded(AccessTools.Method(typeof(TendUtility), nameof(TendUtility.DoTend)), nameof(BeforeTend), nameof(AfterTend));
            Guarded(AccessTools.Method(typeof(Thing), nameof(Thing.Ingested)), nameof(BeforeIngested), nameof(AfterIngested));
            Pair(AccessTools.Method(typeof(Building_NutrientPasteDispenser), nameof(Building_NutrientPasteDispenser.TryDispenseFood)), typeof(PasteTag));
            Pair(AccessTools.Method(typeof(CompRefuelable), nameof(CompRefuelable.Refuel), new[] { typeof(List<Thing>) }), typeof(FuelTag));
            Guarded(AccessTools.Method(typeof(CompRefuelable), nameof(CompRefuelable.EjectFuel)), nameof(BeforeEject), null);
            Guarded(AccessTools.Method(typeof(CompChangeableProjectile), nameof(CompChangeableProjectile.LoadShell)), nameof(BeforeLoadShell), null);
            Guarded(AccessTools.Method(typeof(CompChangeableProjectile), nameof(CompChangeableProjectile.RemoveShell)), nameof(BeforeRemoveShell), null);
            Pair(AccessTools.Method(typeof(CompRottable), "TickInterval"), typeof(RotTag));
            Guarded(AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction)), nameof(BeforeComplete), nameof(Restore));
            Guarded(AccessTools.Method(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor),
                new[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect), typeof(Predicate<IntVec3>), typeof(List<Thing>) }),
                nameof(BeforeLeavings), nameof(Restore));
            foreach (var type in new[] { typeof(Pawn_TraderTracker), typeof(TradeShip), typeof(Caravan_TraderTracker), typeof(Settlement_TraderTracker) })
                Guarded(AccessTools.Method(type, "GiveSoldThingToTrader"), nameof(BeforeSold), nameof(Restore));
            Guarded(AccessTools.Method(typeof(Pawn), nameof(Pawn.ExitMap)), nameof(BeforeExit), nameof(Restore));
        }

        private static void Count(ThingDef? def, long count, ConsumptionReason reason)
        {
            if (def == null || count == 0 || Current.Game == null || Current.ProgramState != ProgramState.Playing) return;
            var ticks = Find.TickManager?.TicksGame;
            if (ticks == null) return;
            ConsumptionState.For(Current.Game).Add(def.defName, reason, count, ticks.Value);
        }

        // Items only: a destroyed building, plant, pawn or corpse is not stock.
        private static bool IsStock(Thing thing) =>
            thing.def.category == ThingCategory.Item && !(thing is Corpse) && !(thing is MinifiedThing);

        private static void Restore(int __state) { context = __state; }

        // The fallback and every attributed destroy. Reads stackCount before
        // Destroy zeroes it.
        private static void BeforeDestroy(Thing __instance)
        {
            if (context == Suppress || __instance.Destroyed || !IsStock(__instance)) return;
            var count = __instance.stackCount;
            if (count <= 0) return;
            if (swallowDef != null && swallowDef == __instance.def && Find.TickManager.TicksGame == swallowTick)
            {
                swallowDef = null;
                return;
            }
            Count(__instance.def, count, context > 0 ? (ConsumptionReason)context : ConsumptionReason.DestroyedOther);
        }

        // Kill's damage def says how a stack died. Anything else keeps the
        // surrounding context (none: destroyed_other).
        private static void BeforeKill(Thing __instance, DamageInfo? dinfo, out int __state)
        {
            __state = context;
            var def = dinfo?.Def;
            if (def == null) return;
            if (def == DamageDefOf.Flame) context = (int)ConsumptionReason.Fire;
            else if (def == DamageDefOf.Deterioration) context = (int)(__instance is Apparel ? ConsumptionReason.ApparelWear : ConsumptionReason.Deterioration);
            else if (def == DamageDefOf.Rotting) context = (int)ConsumptionReason.Rot;
        }

        // The dispenser's hopper stacks are split, not destroyed.
        private static void BeforeSplit(Thing __instance, int count)
        {
            if (context == (int)ConsumptionReason.NutrientPaste && count > 0 && count < __instance.stackCount)
                Count(__instance.def, count, ConsumptionReason.NutrientPaste);
        }

        internal struct Unit
        {
            public int Previous;
            public Thing? Thing;
            public ThingDef? Def;
            public int Before;
        }

        // One unit leaves the medicine stack (the last one destroys it).
        private static void BeforeTend(Medicine medicine, out Unit __state)
        {
            __state = new Unit { Previous = context, Thing = medicine, Def = medicine?.def, Before = medicine != null && !medicine.Destroyed ? medicine.stackCount : -1 };
            context = Suppress;
        }

        private static void AfterTend(Unit __state)
        {
            context = __state.Previous;
            if (__state.Before >= 0 && __state.Thing != null)
                Count(__state.Def, __state.Before - (__state.Thing.Destroyed ? 0 : __state.Thing.stackCount), ConsumptionReason.MedicineTend);
        }

        // Plants and corpses are eaten as terrain and bodies, not stock.
        private static void BeforeIngested(Thing __instance, out Unit __state)
        {
            var counted = !(__instance is Plant) && IsStock(__instance);
            __state = new Unit { Previous = context, Thing = __instance, Def = __instance.def, Before = counted ? __instance.stackCount : -1 };
            context = Suppress;
        }

        private static void AfterIngested(Pawn ingester, Unit __state)
        {
            context = __state.Previous;
            if (__state.Before < 0 || __state.Thing == null) return;
            var left = __state.Thing.Destroyed ? 0 : __state.Thing.stackCount;
            var reason = __state.Def!.IsDrug ? ConsumptionReason.DrugDose
                : ingester?.RaceProps.Humanlike == true ? ConsumptionReason.FoodEaten : ConsumptionReason.AnimalFeed;
            Count(__state.Def, __state.Before - left, reason);
        }

        // Ejected fuel returns to the world; the loaded count is net.
        private static void BeforeEject(CompRefuelable __instance)
        {
            var def = __instance.Props.fuelFilter?.AllowedThingDefs?.FirstOrDefault();
            Count(def, -Mathf.FloorToInt(__instance.Fuel), ConsumptionReason.FuelLoaded);
        }

        // LoadShell replaces the loaded stack; count the added shells.
        private static void BeforeLoadShell(CompChangeableProjectile __instance, ThingDef shell, int count)
        {
            if (shell == null || count <= 0) return;
            var added = count - (__instance.LoadedShell == shell ? __instance.loadedCount : 0);
            if (added <= 0) return;
            Count(shell, added, ConsumptionReason.ShellLoaded);
            if (Find.TickManager != null) { swallowDef = shell; swallowTick = Find.TickManager.TicksGame; }
        }

        private static void BeforeRemoveShell(CompChangeableProjectile __instance)
        {
            if (__instance.LoadedShell != null && __instance.loadedCount > 0)
                Count(__instance.LoadedShell, -__instance.loadedCount, ConsumptionReason.ShellLoaded);
        }

        // The materials delivered to a finished frame are spent on the build.
        private static void BeforeComplete(Frame __instance, out int __state)
        {
            __state = context;
            foreach (var thing in __instance.resourceContainer)
                if (IsStock(thing)) Count(thing.def, thing.stackCount, ConsumptionReason.Construction);
            context = Suppress;
        }

        // A frame's materials the leavings do not return are destroyed inside
        // this call: a failed or deconstructed frame loses them to the build.
        private static void BeforeLeavings(Thing diedThing, out int __state)
        {
            __state = context;
            if (diedThing is Frame) context = (int)ConsumptionReason.Construction;
        }

        // The player sells stock to the trader (silver paid for a purchase too).
        private static void BeforeSold(Thing toGive, int countToGive, out int __state)
        {
            __state = context;
            if (toGive != null && !(toGive is Pawn)) Count(toGive.def, countToGive, ConsumptionReason.Sold);
            context = Suppress;
        }

        // A hostile pawn leaving the map with a carried stack stole it.
        private static void BeforeExit(Pawn __instance, out int __state)
        {
            __state = context;
            var carried = __instance.carryTracker?.CarriedThing;
            if (carried != null && !(carried is Pawn) && __instance.Faction != Faction.OfPlayer && __instance.HostileTo(Faction.OfPlayer))
            {
                if (IsStock(carried)) Count(carried.def, carried.stackCount, ConsumptionReason.Stolen);
                context = Suppress;
            }
        }
    }
}
