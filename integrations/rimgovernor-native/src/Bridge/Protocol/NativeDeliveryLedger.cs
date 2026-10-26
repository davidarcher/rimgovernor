using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using Tick = RimGovernor.Brain.Domain.Tick;

namespace RimGovernor.Native
{
    // Cumulative delivery counters for the player's own production, latched at
    // the production site from the produced stack (plant harvest, fish catch,
    // gathered body resource, laid egg), so hauling and stack merges can never
    // double-count. State lives per loaded game: a load or restart starts a new
    // load token and readers re-baseline. Hunting is counted in two stages: a Kill when
    // a player pawn kills an animal and a Butcher where a butcher recipe makes its
    // products, each keyed by the corpse id. Hooks install on the first facts read;
    // a read that cannot count throws, which fails the colony facts read.
    internal static class NativeDeliveryLedger
    {
        private const string Owner = "rimgovernor.native.deliveries";

        private sealed class Counter { internal long Units; internal double Nutrition; }
        private sealed class State
        {
            internal readonly string LoadToken = Guid.NewGuid().ToString("N");
            internal readonly Dictionary<(RimGovernor.Brain.GameFacts.Observations.DeliveryKind Kind, string Source, string Def), Counter> Counts = new Dictionary<(RimGovernor.Brain.GameFacts.Observations.DeliveryKind, string, string), Counter>();
            internal readonly List<RimGovernor.Brain.GameFacts.Observations.Kill> Kills = new List<RimGovernor.Brain.GameFacts.Observations.Kill>();
            internal readonly List<RimGovernor.Brain.GameFacts.Observations.Butcher> Butchers = new List<RimGovernor.Brain.GameFacts.Observations.Butcher>();
        }

        private static readonly ConditionalWeakTable<Game, State> States = new ConditionalWeakTable<Game, State>();
        private static bool installed;
        // Things placed inside one Gathered call; the stack at placement is the produced amount.
        [ThreadStatic] private static List<(Thing Thing, ThingDef Def, int Count)>? gathering;

        internal static RimGovernor.Brain.GameFacts.Observations.DeliveryLedger Read()
        {
            Install();
            var state = States.GetOrCreateValue(Current.Game ?? throw new InvalidOperationException("No game is loaded."));
            return new RimGovernor.Brain.GameFacts.Observations.DeliveryLedger
            {
                LoadToken = state.LoadToken,
                Counts = [.. state.Counts.OrderBy(r => r.Key.Kind).ThenBy(r => r.Key.Source, StringComparer.Ordinal).ThenBy(r => r.Key.Def, StringComparer.Ordinal)
                    .Select(r => new RimGovernor.Brain.GameFacts.Observations.DeliveryCount { Kind = r.Key.Kind, Source = r.Key.Source, Def = r.Key.Def, Units = r.Value.Units, Nutrition = r.Value.Nutrition })],
                Kills = [.. state.Kills],
                Butchers = [.. state.Butchers],
            };
        }

        private static void Install()
        {
            if (installed) return;
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

        private static bool PlayerWork(Pawn? pawn) => pawn != null && pawn.Faction == Faction.OfPlayerSilentFail;

        private static void Count(RimGovernor.Brain.GameFacts.Observations.DeliveryKind kind, string source, Thing? produced, Pawn? worker = null) { if (produced != null) Count(kind, source, produced.def, produced.stackCount, worker); }

        private static void Count(RimGovernor.Brain.GameFacts.Observations.DeliveryKind kind, string source, ThingDef def, int units, Pawn? worker = null)
        {
            if (Current.Game == null || def == null || units <= 0) return;
            var state = States.GetOrCreateValue(Current.Game);
            var key = (kind, source, def.defName);
            if (!state.Counts.TryGetValue(key, out var counter)) state.Counts[key] = counter = new Counter();
            counter.Units += units;
            if (NativeFoodPolicy.IsFood(def) && def.ingestible.HumanEdible) counter.Nutrition += units * def.GetStatValueAbstract(StatDefOf.Nutrition);
            PawnActivityTracker.Output(kind.ToString(), source, def.defName, units, worker);
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
            if (grower != null) Count(RimGovernor.Brain.GameFacts.Observations.DeliveryKind.Crop, grower.GetUniqueLoadID(), __1, __0);
            else if (__1?.def != null && NativeFoodPolicy.IsFood(__1.def)) Count(RimGovernor.Brain.GameFacts.Observations.DeliveryKind.Forage, plant?.def.defName ?? __1.def.defName, __1, __0);
        }

        // FishingUtility.GetCatchesFor is the catch roll of one completed fishing job. A catch is
        // counted under its water body, the fishing channel it delivers for; the game fishes only
        // in a water body, and a catch outside one has no channel to credit, so it is not counted.
        private static void Caught(Pawn __0, IntVec3 __1, List<Thing> __result)
        {
            if (!PlayerWork(__0) || __result == null) return;
            var body = __0.Map?.waterBodyTracker.WaterBodyAt(__1);
            if (body == null) return;
            var source = body.rootCell.x + "," + body.rootCell.z;
            foreach (var thing in __result) Count(RimGovernor.Brain.GameFacts.Observations.DeliveryKind.Fish, source, thing, __0);
        }

        private static void Laid(CompEggLayer __instance, Thing __result)
        {
            if (__result != null && PlayerWork(__instance.parent as Pawn)) Count(RimGovernor.Brain.GameFacts.Observations.DeliveryKind.AnimalProduct, __instance.parent.def.defName, __result);
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
            foreach (var (_, def, count) in placed) Count(RimGovernor.Brain.GameFacts.Observations.DeliveryKind.AnimalProduct, __instance.parent.def.defName, def, count);
        }

        // Pawn.Kill leaves the corpse on the pawn once it ran. An animal of the wild or the
        // colony killed by a player pawn (a hunt or a slaughter) is one Kill.
        private static void Killed(Pawn __instance, DamageInfo? __0)
        {
            try
            {
                var corpse = __instance.Corpse;
                if (Current.Game == null || corpse == null || !__instance.RaceProps.Animal || !PlayerWork(__0?.Instigator as Pawn)
                    || (__instance.Faction != null && !__instance.Faction.IsPlayer)) return;
                var state = States.GetOrCreateValue(Current.Game);
                var meat = __instance.RaceProps.meatDef;
                state.Kills.Add(new RimGovernor.Brain.GameFacts.Observations.Kill { CorpseId = corpse.GetUniqueLoadID(), Pawn = new PawnId(__instance.GetUniqueLoadID()),
                    Race = __instance.def.defName, BodySize = __instance.BodySize,
                    PotentialNutrition = meat == null ? 0 : Math.Max(0, __instance.GetStatValue(StatDefOf.MeatAmount)) * meat.GetStatValueAbstract(StatDefOf.Nutrition),
                    Tick = new Tick(Find.TickManager.TicksGame) });
            }
            catch (Exception error) { ModLog.Error("observe", "Kill counting failed: " + error); }
        }

        // MakeRecipeProducts is an iterator: the products are read as the caller takes them, before
        // it places or merges them. A butcher recipe over one corpse is one Butcher at the end.
        private static IEnumerable<Thing> Butchered(IEnumerable<Thing> __result, RecipeDef __0, Pawn __1, List<Thing> __2)
        {
            var bill = PlayerWork(__1) && __1.CurJob?.bill?.recipe == __0 ? __1.CurJob.bill.GetUniqueLoadID() : null;
            var butcher = NativeRecipeRoles.ButcherFlesh(__0) && PlayerWork(__1) && __2 != null && __2.Count == 1 && __2[0] is Corpse;
            var corpseId = butcher ? (__2 ?? throw new System.InvalidOperationException("Required native value is unavailable: __2."))[0].GetUniqueLoadID() : null;
            long meat = 0, leather = 0;
            double nutrition = 0;
            foreach (var product in __result)
            {
                if (bill != null) Count(RimGovernor.Brain.GameFacts.Observations.DeliveryKind.Recipe, bill, product, __1);
                if (butcher && product?.def != null)
                {
                    if (product.def.IsMeat) { meat += product.stackCount; nutrition += product.stackCount * product.def.GetStatValueAbstract(StatDefOf.Nutrition); }
                    else if (product.def.IsLeather) leather += product.stackCount;
                }
                yield return (product ?? throw new System.InvalidOperationException("Required native value is unavailable: product."));
            }
            if (corpseId == null || Current.Game == null) yield break;
            States.GetOrCreateValue(Current.Game).Butchers.Add(new RimGovernor.Brain.GameFacts.Observations.Butcher { CorpseId = corpseId, MeatUnits = meat, MeatNutrition = nutrition, LeatherUnits = leather });
        }
    }
}
