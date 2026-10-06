#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Cumulative delivery counters for the player's own production, latched at
    // the production site from the produced stack (plant harvest, fish catch,
    // gathered body resource, laid egg), so hauling and stack merges can never
    // double-count. State lives per loaded game: a load or restart starts a new
    // epoch and readers re-baseline. Hunting is counted in two stages: a KILL when
    // a player pawn kills an animal and a BUTCHER where a butcher recipe makes its
    // products, each keyed by the corpse id. Hooks install on the first facts read.
    internal static class NativeDeliveryLedger
    {
        private const string Owner = "rimgovernor.native.deliveries";
        private const int MaxKeys = 512;
        private const int MaxRecords = 256;
        private const string OtherKey = "other";

        private sealed class Counter { internal long Units; internal double Nutrition; internal int LastTick; }
        private sealed class State
        {
            internal readonly string Epoch = Guid.NewGuid().ToString("N");
            internal readonly Dictionary<(Obs.DeliverySourceKind Kind, string Source, string Def), Counter> Rows = new Dictionary<(Obs.DeliverySourceKind, string, string), Counter>();
            internal readonly Counter Other = new Counter();
            internal ulong Lost;
            internal readonly List<Obs.KillRecord> Kills = new List<Obs.KillRecord>();
            internal readonly List<Obs.ButcherRecord> Butchers = new List<Obs.ButcherRecord>();
            internal ulong KillsTotal, ButchersTotal;
        }

        private static readonly ConditionalWeakTable<Game, State> States = new ConditionalWeakTable<Game, State>();
        private static bool installed;
        // Things placed inside one Gathered call; the stack at placement is the produced amount.
        [ThreadStatic] private static List<(Thing Thing, ThingDef Def, int Count)>? gathering;

        internal static Obs.DeliveryLedgerSection Read()
        {
            try
            {
                Install();
                if (!installed || Current.Game == null) return Unavailable("Delivery counting is not installed.");
                var state = States.GetOrCreateValue(Current.Game);
                var facts = new Obs.DeliveryLedgerFacts { Epoch = state.Epoch, Lost = state.Lost };
                foreach (var row in state.Rows.OrderBy(r => r.Key.Kind).ThenBy(r => r.Key.Source, StringComparer.Ordinal).ThenBy(r => r.Key.Def, StringComparer.Ordinal))
                    facts.Rows.Add(Row(row.Value, row.Key.Kind, row.Key.Source, row.Key.Def));
                facts.Kills.Add(state.Kills);
                facts.Butchers.Add(state.Butchers);
                facts.KillsTotal = state.KillsTotal;
                facts.ButchersTotal = state.ButchersTotal;
                if (state.Lost > 0) facts.Other = Row(state.Other, null, OtherKey, OtherKey);
                return new Obs.DeliveryLedgerSection { Observed = facts };
            }
            catch (Exception error)
            {
                ModLog.Error("observe", "Delivery ledger failed: " + error);
                return Unavailable("Delivery ledger could not be read.");
            }
        }

        private static Obs.DeliveryLedgerSection Unavailable(string detail) =>
            new Obs.DeliveryLedgerSection { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = detail } };

        private static Obs.DeliveryRow Row(Counter c, Obs.DeliverySourceKind? kind, string source, string def)
        {
            var row = new Obs.DeliveryRow { SourceId = source, DefName = def, Units = c.Units, Nutrition = c.Nutrition, LastTick = c.LastTick };
            if (kind != null) row.SourceKind = kind.Value;
            return row;
        }

        // The kill hook is shared: native rules (NativeRuleRuntime) trigger from it.
        internal static bool EnsureInstalled() { Install(); return installed; }

        private static void Install()
        {
            if (installed) return;
            try
            {
                var harmony = new Harmony(Owner);
                harmony.Patch(AccessTools.Method(typeof(QuestManager), nameof(QuestManager.Notify_PlantHarvested), new[] { typeof(Pawn), typeof(Thing) }),
                    postfix: new HarmonyMethod(typeof(NativeDeliveryLedger), nameof(Harvested)));
                harmony.Patch(AccessTools.Method(typeof(FishingUtility), nameof(FishingUtility.GetCatchesFor)),
                    postfix: new HarmonyMethod(typeof(NativeDeliveryLedger), nameof(Caught)));
                harmony.Patch(AccessTools.Method(typeof(CompEggLayer), nameof(CompEggLayer.ProduceEgg)),
                    postfix: new HarmonyMethod(typeof(NativeDeliveryLedger), nameof(Laid)));
                harmony.Patch(AccessTools.Method(typeof(CompHasGatherableBodyResource), nameof(CompHasGatherableBodyResource.Gathered)),
                    prefix: new HarmonyMethod(typeof(NativeDeliveryLedger), nameof(GatherStart)),
                    postfix: new HarmonyMethod(typeof(NativeDeliveryLedger), nameof(GatherEnd)));
                harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.Kill)),
                    postfix: new HarmonyMethod(typeof(NativeDeliveryLedger), nameof(Killed)));
                harmony.Patch(AccessTools.Method(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts)),
                    postfix: new HarmonyMethod(typeof(NativeDeliveryLedger), nameof(Butchered)));
                foreach (var place in typeof(GenPlace).GetMethods().Where(m => m.Name == nameof(GenPlace.TryPlaceThing)))
                    harmony.Patch(place, prefix: new HarmonyMethod(typeof(NativeDeliveryLedger), nameof(Placed)));
                installed = true;
            }
            catch (Exception error) { ModLog.Error("observe", "Delivery counting unavailable: " + error); }
        }

        private static bool PlayerWork(Pawn? pawn) => pawn != null && pawn.Faction == Faction.OfPlayerSilentFail;

        private static void Count(Obs.DeliverySourceKind kind, string source, Thing? produced) { if (produced != null) Count(kind, source, produced.def, produced.stackCount); }

        private static void Count(Obs.DeliverySourceKind kind, string source, ThingDef def, int units)
        {
            if (Current.Game == null || def == null || units <= 0) return;
            var state = States.GetOrCreateValue(Current.Game);
            var key = (kind, source, def.defName);
            if (!state.Rows.TryGetValue(key, out var counter))
            {
                if (state.Rows.Count >= MaxKeys) { counter = state.Other; state.Lost++; }
                else state.Rows[key] = counter = new Counter();
            }
            counter.Units += units;
            if (NativeFoodPolicy.IsFood(def) && def.ingestible.HumanEdible) counter.Nutrition += units * def.GetStatValueAbstract(StatDefOf.Nutrition);
            counter.LastTick = Find.TickManager.TicksGame;
        }

        // QuestManager.Notify_PlantHarvested carries the freshly made harvest stack.
        // A plant outside a growing zone or basin is wild: forage, counted only when edible.
        private static void Harvested(Pawn __0, Thing __1)
        {
            if (!PlayerWork(__0)) return;
            var plant = __0.jobs?.curDriver is JobDriver_PlantWork ? __0.CurJob.targetA.Thing as Plant : null;
            var grower = plant == null || plant.Map == null ? null
                : (ILoadReferenceable?)(plant.Map.zoneManager.ZoneAt(plant.Position) as Zone_Growing)
                    ?? plant.Position.GetThingList(plant.Map).OfType<Building_PlantGrower>().FirstOrDefault();
            if (grower != null) Count(Obs.DeliverySourceKind.Crop, grower.GetUniqueLoadID(), __1);
            else if (__1?.def != null && NativeFoodPolicy.IsFood(__1.def)) Count(Obs.DeliverySourceKind.Forage, plant?.def.defName ?? __1.def.defName, __1);
        }

        // FishingUtility.GetCatchesFor is the catch roll of one completed fishing job.
        private static void Caught(Pawn __0, IntVec3 __1, List<Thing> __result)
        {
            if (!PlayerWork(__0) || __result == null) return;
            var body = __0.Map?.waterBodyTracker.WaterBodyAt(__1);
            var source = body == null ? "unknown" : body.rootCell.x + "," + body.rootCell.z;
            foreach (var thing in __result) Count(Obs.DeliverySourceKind.Fish, source, thing);
        }

        private static void Laid(CompEggLayer __instance, Thing __result)
        {
            if (__result != null && PlayerWork(__instance.parent as Pawn)) Count(Obs.DeliverySourceKind.AnimalProduct, __instance.parent.def.defName, __result);
        }

        // Gathered makes and places the stacks itself, so its placements are the production.
        private static void GatherStart() { gathering = new List<(Thing, ThingDef, int)>(); }

        // The stack is read as it is placed, before a merge can change it; one call may reach both overloads.
        private static void Placed(Thing thing) { if (gathering != null && thing != null && !gathering.Any(p => p.Thing == thing)) gathering.Add((thing, thing.def, thing.stackCount)); }

        private static void GatherEnd(CompHasGatherableBodyResource __instance)
        {
            var placed = gathering;
            gathering = null;
            if (placed == null || !PlayerWork(__instance.parent as Pawn)) return;
            foreach (var (_, def, count) in placed) Count(Obs.DeliverySourceKind.AnimalProduct, __instance.parent.def.defName, def, count);
        }

        private static void Record<T>(List<T> window, T record) { window.Add(record); if (window.Count > MaxRecords) window.RemoveAt(0); }

        // Pawn.Kill leaves the corpse on the pawn once it ran. An animal of the wild or the
        // colony killed by a player pawn (a hunt or a slaughter) is one KILL.
        private static void Killed(Pawn __instance, DamageInfo? __0)
        {
            try
            {
                var corpse = __instance.Corpse;
                if (Current.Game == null || corpse == null || !__instance.RaceProps.Animal || !PlayerWork(__0?.Instigator as Pawn)
                    || (__instance.Faction != null && !__instance.Faction.IsPlayer)) return;
                var state = States.GetOrCreateValue(Current.Game);
                var meat = __instance.RaceProps.meatDef;
                var record = new Obs.KillRecord { CorpseId = corpse.GetUniqueLoadID(), PawnId = __instance.GetUniqueLoadID(), Race = __instance.def.defName, BodySize = __instance.BodySize,
                    PotentialNutrition = meat == null ? 0 : Math.Max(0, __instance.GetStatValue(StatDefOf.MeatAmount)) * meat.GetStatValueAbstract(StatDefOf.Nutrition),
                    Tick = Find.TickManager.TicksGame };
                state.KillsTotal++;
                Record(state.Kills, record);
                if (__instance.Faction == null) NativeRuleRuntime.OnPreyKilled(__0?.Instigator as Pawn, __instance);
            }
            catch (Exception error) { ModLog.Error("observe", "Kill counting failed: " + error); }
        }

        // MakeRecipeProducts is an iterator: the products are read as the caller takes them, before
        // it places or merges them. A butcher recipe over one corpse is one BUTCHER at the end.
        private static IEnumerable<Thing> Butchered(IEnumerable<Thing> __result, RecipeDef __0, Pawn __1, List<Thing> __2)
        {
            var butcher = NativeRecipeRoles.ButcherFlesh(__0) && PlayerWork(__1) && __2 != null && __2.Count == 1 && __2[0] is Corpse;
            var corpseId = butcher ? __2![0].GetUniqueLoadID() : null;
            ThingDef? meatDef = null;
            long meat = 0, leather = 0;
            double nutrition = 0;
            foreach (var product in __result)
            {
                if (butcher && product != null && product.def != null)
                {
                    if (product.def.IsMeat) { meatDef = product.def; meat += product.stackCount; nutrition += product.stackCount * product.def.GetStatValueAbstract(StatDefOf.Nutrition); }
                    else if (product.def.IsLeather) leather += product.stackCount;
                }
                yield return product!;
            }
            if (corpseId == null || Current.Game == null) yield break;
            var record = new Obs.ButcherRecord { CorpseId = corpseId, Recipe = __0.defName, MeatUnits = meat, MeatNutrition = nutrition,
                LeatherUnits = leather, Tick = Find.TickManager.TicksGame };
            if (meatDef != null) record.MeatDef = meatDef.defName;
            var state = States.GetOrCreateValue(Current.Game);
            state.ButchersTotal++;
            Record(state.Butchers, record);
        }
    }
}
