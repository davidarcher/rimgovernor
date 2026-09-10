using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Read-only test evidence, including references retained after native rot destruction.
    public sealed class FoodObservationFixture
    {
        private static Game game;
        private static Map map;
        private static bool patched;
        private static readonly Dictionary<string, Thing> watched = new Dictionary<string, Thing>();
        private static readonly Dictionary<string, string> origins = new Dictionary<string, string>();
        private static readonly List<object> meals = new List<object>();
        private static string Origin(Thing thing)
        {
            var id = thing.GetUniqueLoadID();
            return origins.TryGetValue(id, out var origin) ? origin : id;
        }

        private static void Split(Thing __instance, Thing __result)
        {
            if (Current.Game == game && __result != null && __instance.def.IsNutritionGivingIngestible)
                origins[__result.GetUniqueLoadID()] = Origin(__instance);
        }

        private static void Ate(Thing __instance, Pawn __0, float __result)
        {
            if (Current.Game == game && __0.MapHeld == map && __result > 0 && meals.Count < 10000)
                meals.Add(new { tick = Find.TickManager.TicksGame, food = __instance.GetUniqueLoadID(),
                    origin = Origin(__instance), defName = __instance.def.defName,
                    pawn = __0.GetUniqueLoadID(), nutrition = __result });
        }

        [Tool("test/food_observe", Description = "Read ordinary native ingestion and retained food rot state. Test-only; never changes food, pawns, temperatures or time.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Optional spawned food thingId to retain for later rot observation.")] string target = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Find.CurrentMap == null) throw new InvalidOperationException("No loaded map");
                if (Current.Game != game || Find.CurrentMap != map)
                {
                    game = Current.Game; map = Find.CurrentMap;
                    watched.Clear(); origins.Clear(); meals.Clear();
                }
                if (!patched)
                {
                    var harmony = new Harmony("rimbot.test.food-observation");
                    harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.SplitOff)),
                        postfix: new HarmonyMethod(typeof(FoodObservationFixture), nameof(Split)));
                    harmony.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Ingested)),
                        postfix: new HarmonyMethod(typeof(FoodObservationFixture), nameof(Ate)));
                    patched = true;
                }
                if (!string.IsNullOrEmpty(target) && !watched.ContainsKey(target))
                {
                    var food = map.listerThings.AllThings.Single(t => t.GetUniqueLoadID() == target);
                    if (!food.def.IsNutritionGivingIngestible || food.TryGetComp<CompRottable>() == null)
                        throw new ArgumentException("Target is not observed perishable food");
                    watched.Add(target, food);
                }
                return new { success = true, tick = Find.TickManager.TicksGame, meals,
                    truncated = meals.Count >= 10000,
                    watched = watched.Values.Select(t => new {
                        id = t.GetUniqueLoadID(), defName = t.def.defName, count = t.stackCount,
                        destroyed = t.Destroyed, spawned = t.Spawned,
                        forbidden = t.Spawned ? (bool?)t.IsForbidden(Faction.OfPlayer) : null,
                        x = t.Position.x, z = t.Position.z,
                        temperature = t.Spawned ? (float?)t.AmbientTemperature : null,
                        stage = t.TryGetComp<CompRottable>().Stage.ToString(),
                        rotProgress = t.TryGetComp<CompRottable>().RotProgress,
                        rotTicks = t.Spawned ? (int?)t.TryGetComp<CompRottable>().TicksUntilRotAtCurrentTemp : null
                    }).ToList() };
            }, cancellationToken);
        }
    }
}
