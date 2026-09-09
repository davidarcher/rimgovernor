using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    internal static class ProductionPolicyGuard
    {
        private static bool patched;
        internal static ProductionPolicyState State()
        {
            if (Current.Game == null) return null;
            var state = Current.Game.GetComponent<ProductionPolicyState>();
            if (state == null) { state = new ProductionPolicyState(Current.Game); Current.Game.components.Add(state); }
            return state;
        }
        internal static void EnsurePatched()
        {
            if (patched) return;
            var method = AccessTools.Method(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients");
            if (method == null) throw new InvalidOperationException("Native ingredient selection unavailable");
            new Harmony("rimbot.production-policy").Patch(method,
                prefix: new HarmonyMethod(typeof(ProductionPolicyGuard), nameof(BeforeSelection)),
                postfix: new HarmonyMethod(typeof(ProductionPolicyGuard), nameof(AfterSelection)),
                finalizer: new HarmonyMethod(typeof(ProductionPolicyGuard), nameof(RestoreFilter)));
            new Harmony("rimbot.production-policy").Patch(AccessTools.Method(typeof(Toils_Recipe), "FinishRecipeAndStartStoringProduct"),
                postfix: new HarmonyMethod(typeof(ProductionPolicyGuard), nameof(GuardConsumption)));
            patched = true;
        }
        internal static string Key(Map map, string resource) => map.uniqueID + ":" + resource;
        internal static Dictionary<string, int> Stock(Map map) => map.listerThings
            .ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer))
            .GroupBy(t => t.def.defName).ToDictionary(g => g.Key, g => g.Sum(t => t.stackCount));
        internal static Dictionary<string, int> Budgets(Map map, Pawn worker = null)
        {
            var state = State(); var stock = Stock(map); var prefix = map.uniqueID + ":";
            Action<string, int> subtract = (name, count) => { stock.TryGetValue(name, out var current); stock[name] = current - count; };
            foreach (var floor in state.Floors.Where(p => p.Key.StartsWith(prefix))) subtract(floor.Key.Substring(prefix.Length), floor.Value);
            if (Supervisor.IsActive) foreach (var held in state.Commitments.Where(p => p.Key.StartsWith(prefix)))
                subtract(held.Key.Substring(prefix.Length), held.Value);
            foreach (var thing in map.listerThings.AllThings)
            {
                if (!(thing is IConstructible construction) || thing is Blueprint_Install) continue;
                var costs = construction.TotalMaterialCost();
                if (costs != null) foreach (var cost in costs)
                    subtract(cost.thingDef.defName, BridgeCommon.ConstructibleStillNeeded(construction, cost.thingDef, cost.count));
            }
            // Spawned ingredients promised to another live bill job are not free stock.
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.Where(p => p != worker))
            {
                var job = pawn.CurJob;
                if (job?.bill == null || job.targetQueueB == null || job.countQueue == null) continue;
                for (var i = 0; i < Math.Min(job.targetQueueB.Count, job.countQueue.Count); i++)
                    if (job.targetQueueB[i].Thing is Thing t && t.Spawned) subtract(t.def.defName, job.countQueue[i]);
                if (job.placedThings != null) foreach (var placed in job.placedThings)
                    if (placed.thing.Spawned) subtract(placed.thing.def.defName, placed.Count);
            }
            foreach (var key in state.Stopped.Where(k => k.StartsWith(prefix))) stock[key.Substring(prefix.Length)] = 0;
            return stock;
        }
        private static int Budget(Dictionary<string, int> budgets, ThingDef def) =>
            budgets.TryGetValue(def.defName, out var n) ? Math.Max(0, n) : 0;
        private static void BeforeSelection(Bill bill, Pawn pawn, Thing billGiver, out ThingFilter __state)
        {
            __state = null;
            if (!(bill is Bill_Production) || billGiver.Map == null || !Active(billGiver.Map)) return;
            __state = bill.ingredientFilter;
            var filter = new ThingFilter(); filter.CopyAllowancesFrom(__state);
            var budgets = Budgets(billGiver.Map, pawn);
            foreach (var def in filter.AllowedThingDefs.ToList())
            {
                var needs = bill.recipe.ingredients.Where(i => i.filter.Allows(def))
                    .Select(i => i.CountRequiredOfFor(def, bill.recipe, bill)).ToList();
                if (needs.Count != 0 && Budget(budgets, def) < (bill.recipe.allowMixingIngredients ? 1 : needs.Max()))
                    filter.SetAllow(def, false);
            }
            bill.ingredientFilter = filter;
        }
        private static void AfterSelection(Bill bill, Pawn pawn, Thing billGiver, List<ThingCount> chosen, ref bool __result)
        {
            if (!(bill is Bill_Production) || !__result || billGiver.Map == null || !Active(billGiver.Map)) return;
            var budgets = Budgets(billGiver.Map, pawn);
            if (chosen.GroupBy(t => t.Thing.def).Any(g => g.Sum(t => t.Count) > Budget(budgets, g.Key)))
            { chosen.Clear(); __result = false; }
        }
        private static void RestoreFilter(Bill bill, ThingFilter __state)
        { if (__state != null) bill.ingredientFilter = __state; }
        private static void GuardConsumption(Toil __result)
        {
            var toil = __result; var original = toil.initAction;
            toil.initAction = () => {
                var actor = toil.actor; var job = actor.CurJob;
                if (job?.bill is Bill_Production && Active(actor.Map))
                {
                    var chosen = new List<ThingCount>();
                    if (job.GetTarget(TargetIndex.B).Thing is UnfinishedThing unfinished)
                        chosen.AddRange(unfinished.ingredients.Select(t => new ThingCount(t, t.stackCount)));
                    else if (job.placedThings != null)
                        chosen.AddRange(job.placedThings.Select(t => new ThingCount(t.thing, t.Count)));
                    var budgets = Budgets(actor.Map, actor);
                    foreach (var group in chosen.GroupBy(t => t.Thing.def))
                    {
                        var stopped = State().Stopped.Contains(Key(actor.Map, group.Key.defName));
                        var held = group.Where(t => !t.Thing.Spawned).Sum(t => t.Count);
                        if (stopped || group.Sum(t => t.Count) > (budgets.TryGetValue(group.Key.defName, out var free) ? free : 0) + held)
                        { actor.jobs.EndCurrentJob(JobCondition.InterruptForced); return; }
                    }
                }
                original();
            };
        }
        private static bool Active(Map map)
        {
            var state = State(); var prefix = map.uniqueID + ":";
            return state != null && (state.Floors.Keys.Any(k => k.StartsWith(prefix)) || (Supervisor.IsActive && state.Commitments.Keys.Any(k => k.StartsWith(prefix))) || state.Stopped.Any(k => k.StartsWith(prefix)) || map.listerThings.AllThings.Any(t => t is IConstructible && !(t is Blueprint_Install)));
        }
    }

    public sealed class ProductionPolicyTools
    {
        public ProductionPolicyTools() { ProductionPolicyGuard.EnsurePatched(); }
        [Tool("home/production_policy", Title = "Enforce production resource budgets",
            Description = "Set map-scoped resource floors and stopped inputs for ordinary bill ingredient selection. Exact native selected counts are checked before a new bill job is admitted. Existing production jobs are interrupted when budgets change; bill settings and unfinished work remain. Policies persist with the native save. Paused exact colony/load/map required; dry run by default.")]
        public async Task<object> Policy(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact colony ID")] string colonyId,
            [ToolParameter(Description = "Exact load token")] string loadToken,
            [ToolParameter(Description = "Exact map ID")] int mapId,
            [ToolParameter(Description = "Complete comma separated DefName=quantity persistent player reserve floors. Empty clears.")] string floors = "",
            [ToolParameter(Description = "Transient unissued construction commitments, DefName=quantity CSV. Applied only during a supervised clock lease; not saved in native game.")] string commitments = "",
            [ToolParameter(Description = "Complete comma separated stopped input DefNames. Includes defense-only inputs because routine production has no defensive ownership.")] string stopped = "",
            [ToolParameter(Description = "Preview only", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (map == null || identity == null || identity.ColonyId != colonyId || identity.LoadToken != loadToken
                    || map.uniqueID != mapId || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
                    return new { success = false, error = "Paused colony/load/map identity required" };
                var parsed = new Dictionary<string, int>(); var held = new Dictionary<string, int>(); var stops = new List<string>();
                foreach (var part in (floors ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var row = part.Split('=');
                    if (row.Length != 2 || !int.TryParse(row[1], out var amount) || amount < 0
                        || DefDatabase<ThingDef>.GetNamedSilentFail(row[0]) == null || parsed.ContainsKey(row[0]))
                        return new { success = false, error = "Invalid or duplicate resource floor" };
                    parsed[row[0]] = amount;
                }
                foreach (var part in (commitments ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var row = part.Split('=');
                    if (row.Length != 2 || !int.TryParse(row[1], out var amount) || amount < 0
                        || DefDatabase<ThingDef>.GetNamedSilentFail(row[0]) == null || held.ContainsKey(row[0]))
                        return new { success = false, error = "Invalid or duplicate commitment" };
                    held[row[0]] = amount;
                }
                foreach (var resource in (stopped ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (DefDatabase<ThingDef>.GetNamedSilentFail(resource) == null || stops.Contains(resource))
                        return new { success = false, error = "Invalid or duplicate stopped resource" };
                    stops.Add(resource);
                }
                ProductionPolicyGuard.EnsurePatched();
                var state = ProductionPolicyGuard.State(); var prefix = mapId + ":";
                var current = state.Floors.Where(p => p.Key.StartsWith(prefix)).ToDictionary(p => p.Key.Substring(prefix.Length), p => p.Value);
                var currentStops = state.Stopped.Where(p => p.StartsWith(prefix)).Select(p => p.Substring(prefix.Length)).OrderBy(p => p);
                var priorHolds = state.Commitments.Where(p => p.Key.StartsWith(prefix)).ToDictionary(p => p.Key.Substring(prefix.Length), p => p.Value);
                var heldChanged = priorHolds.Count != held.Count || priorHolds.Any(p => !held.TryGetValue(p.Key, out var n) || n != p.Value);
                var changed = current.Count != parsed.Count || current.Any(p => !parsed.TryGetValue(p.Key, out var n) || n != p.Value)
                    || !currentStops.SequenceEqual(stops.OrderBy(p => p));
                var interrupted = new List<string>();
                if (!dryRun && (changed || heldChanged))
                {
                    foreach (var key in state.Floors.Keys.Where(k => k.StartsWith(prefix)).ToList()) state.Floors.Remove(key);
                    foreach (var key in state.Commitments.Keys.Where(k => k.StartsWith(prefix)).ToList()) state.Commitments.Remove(key);
                    foreach (var pair in held) state.Commitments[prefix + pair.Key] = pair.Value;
                    state.Stopped.RemoveAll(k => k.StartsWith(prefix));
                    foreach (var pair in parsed) state.Floors[prefix + pair.Key] = pair.Value;
                    state.Stopped.AddRange(stops.Select(s => prefix + s));
                    if (changed || Supervisor.IsActive) foreach (var pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p.CurJob?.bill != null).ToList())
                    { interrupted.Add(pawn.ThingID); pawn.jobs.EndCurrentJob(JobCondition.InterruptForced); }
                }
                return new { success = true, dryRun, changed, floors = parsed, commitments = held, stopped = stops, interrupted,
                    tick = Find.TickManager.TicksGame, mapId, loadToken };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
