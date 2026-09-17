#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Typed bill-stack and recipe census over every player bill giver, the
    /// reads behind ReadBills/ReadRecipes in observations.proto. A recipe read
    /// without bench_id is the definition catalog: which player-buildable
    /// benches host a recipe, and whether its research is complete, so a
    /// planner can stage a bench before one exists.
    /// </summary>
    public sealed class NativeBillObservationTools
    {
        private const string BillsToolName = "rimgovernor/observations_read_bills";
        private const string RecipesToolName = "rimgovernor/observations_read_recipes";
        private const int Limit = 256;

        [Tool(BillsToolName, Title = "Read typed bill stacks", Description = "Every reachable player bill giver with its ordered bill stack and exact stack CAS token; optional bench_id narrows to one bench.")]
        [ToolResponse("payload", "string", "Official ProtoJSON BillsReply.", Always = true)]
        public async Task<object> ReadBills(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON BillsRequest string.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, BillsToolName, request, Obs.BillsRequest.Parser, out var parsed, out var failure)
                || !ValidateBills(parsed, out failure)) return ProtoBoundary.Encode(new Obs.BillsReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.BillsReply { Failure = error });
                try
                {
                    var player = Faction.OfPlayerSilentFail;
                    if (player == null || map.listerThings == null || map.mapPawns == null)
                        return ProtoBoundary.Encode(new Obs.BillsReply { Unavailable = Missing(Common.UnavailableReason.NativeComponentMissing, "Player faction or map listers are unavailable.") });
                    var snapshot = new Obs.BillsSnapshot { Context = context };
                    var benches = Givers(map, player).Where(t => !parsed.HasBenchId || t.GetUniqueLoadID() == parsed.BenchId).ToList();
                    var limit = parsed.Page?.HasLimit == true ? (int)parsed.Page.Limit : Limit;
                    if (benches.Count > limit) throw new ReadLimit("Bill giver census exceeds the page limit; narrow with bench_id.");
                    foreach (var bench in benches)
                    {
                        var giver = (IBillGiver)bench;
                        if (giver.BillStack.Count > 15) throw new ReadLimit("Bill stack exceeds 15 bills.");
                        var row = new Obs.BillStack { Snapshot = NativeProductionBills.Snapshot(bench, giver, context), Bench = Entity(bench),
                            Usable = NativeProductionBills.Usable(bench), Capacity = 15, Completeness = Complete(giver.BillStack.Count, 0) };
                        if (!row.Usable) row.UnusableReason = bench.IsBurning() ? "burning" : "not_usable_for_bills";
                        for (var index = 0; index < giver.BillStack.Count; index++) row.Bills.Add(NativeProductionBills.BillRow(giver.BillStack.Bills[index], index));
                        snapshot.Benches.Add(row);
                    }
                    snapshot.Completeness = Complete(snapshot.Benches.Count, 0);
                    return Encode(new Obs.BillsReply { Observed = snapshot });
                }
                catch (ReadLimit e) { return ProtoBoundary.Encode(new Obs.BillsReply { Unavailable = Missing(Common.UnavailableReason.LimitExceeded, e.Message) }); }
                catch (Exception e) { return ProtoBoundary.Encode(new Obs.BillsReply { Unavailable = Missing(Common.UnavailableReason.ReadFailed, Failed("Bill stacks", e)) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool(RecipesToolName, Title = "Read typed recipes", Description = "With bench_id: that bench's recipes with products, ingredients, work and the work type a worker must enable. Without: the recipe definition catalog with hosting player-buildable bench definitions, optionally narrowed to recipes producing product_def.")]
        [ToolResponse("payload", "string", "Official ProtoJSON RecipesReply.", Always = true)]
        public async Task<object> ReadRecipes(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RecipesRequest string.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, RecipesToolName, request, Obs.RecipesRequest.Parser, out var parsed, out var failure)
                || !ValidateRecipes(parsed, out failure)) return ProtoBoundary.Encode(new Obs.RecipesReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.RecipesReply { Failure = error });
                try
                {
                    var player = Faction.OfPlayerSilentFail;
                    if (player == null || map.listerThings == null)
                        return ProtoBoundary.Encode(new Obs.RecipesReply { Unavailable = Missing(Common.UnavailableReason.NativeComponentMissing, "Player faction or map listers are unavailable.") });
                    var limit = parsed.Page?.HasLimit == true ? (int)parsed.Page.Limit : Limit;
                    var snapshot = new Obs.RecipesSnapshot { Context = context };
                    if (parsed.HasBenchId)
                    {
                        var bench = Givers(map, player).FirstOrDefault(t => t.GetUniqueLoadID() == parsed.BenchId);
                        if (bench == null) return ProtoBoundary.Encode(new Obs.RecipesReply { Unavailable = Missing(Common.UnavailableReason.NativeComponentMissing, "Bench is not a reachable player bill giver.") });
                        var recipes = bench.def.AllRecipes.OrderBy(r => r.defName, StringComparer.Ordinal).ToList();
                        if (recipes.Count > limit) throw new ReadLimit("Bench recipe list exceeds the page limit.");
                        snapshot.Snapshot = NativeProductionBills.Snapshot(bench, (IBillGiver)bench, context);
                        foreach (var recipe in recipes) snapshot.Recipes.Add(Row(recipe, bench.def, bench));
                    }
                    else
                    {
                        var recipes = DefDatabase<RecipeDef>.AllDefsListForReading
                            .Where(r => r.products != null && r.products.Count > 0 && (!parsed.HasProductDef || r.products.Any(p => p.thingDef?.defName == parsed.ProductDef)))
                            .Where(r => Hosts(r).Count > 0).OrderBy(r => r.defName, StringComparer.Ordinal).ToList();
                        if (recipes.Count > limit) throw new ReadLimit("Recipe catalog exceeds the page limit; narrow with product_def.");
                        snapshot.Snapshot = new Obs.SnapshotRef { Context = context.Clone() };
                        foreach (var recipe in recipes)
                        {
                            var hosts = Hosts(recipe);
                            var row = Row(recipe, hosts[0], null);
                            foreach (var host in hosts) row.BenchDefs.Add(Id(host.defName));
                            snapshot.Recipes.Add(row);
                        }
                    }
                    snapshot.Completeness = Complete(snapshot.Recipes.Count, 0);
                    return Encode(new Obs.RecipesReply { Observed = snapshot });
                }
                catch (ReadLimit e) { return ProtoBoundary.Encode(new Obs.RecipesReply { Unavailable = Missing(Common.UnavailableReason.LimitExceeded, e.Message) }); }
                catch (Exception e) { return ProtoBoundary.Encode(new Obs.RecipesReply { Unavailable = Missing(Common.UnavailableReason.ReadFailed, Failed("Recipes", e)) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool ValidateBills(Obs.BillsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required; bench_id must be an identifier and page limit 1..256.");
            return request?.Scope?.ExpectedIdentity != null && (!request.HasBenchId || ProtoBoundary.IsIdentifier(request.BenchId))
                && (request.Page == null || !request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= Limit);
        }

        internal static bool ValidateRecipes(Obs.RecipesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required; bench_id/product_def must be identifiers and page limit 1..256.");
            return request?.Scope?.ExpectedIdentity != null && (!request.HasBenchId || ProtoBoundary.IsIdentifier(request.BenchId))
                && (!request.HasProductDef || ProtoBoundary.IsIdentifier(request.ProductDef))
                && (request.Page == null || !request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= Limit);
        }

        // Player bill givers a free colonist could walk to, in stable thing order.
        private static List<Thing> Givers(Map map, Faction player)
        {
            var workers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.InMentalState && !p.Drafted).ToList();
            return map.listerThings.AllThings.Where(t => t.Spawned && t is IBillGiver && t.Faction == player && !t.Position.Fogged(map) && !t.IsForbidden(player)
                && workers.Any(p => p.CanReach(t, Verse.AI.PathEndMode.InteractionCell, Danger.None))).OrderBy(t => t.thingIDNumber).ToList();
        }

        // Player-buildable building definitions that host the recipe.
        internal static List<ThingDef> Hosts(RecipeDef recipe) => recipe.AllRecipeUsers
            .Where(d => d.category == ThingCategory.Building && d.BuildableByPlayer && typeof(IBillGiver).IsAssignableFrom(d.thingClass))
            .Distinct().OrderBy(d => d.defName, StringComparer.Ordinal).ToList();

        /// <summary>
        /// The work type whose DoBill giver serves this bench definition, so a
        /// worker must have it enabled to take the bill; null when no giver
        /// serves it or the recipe demands a different giver work type.
        /// </summary>
        internal static WorkTypeDef? WorkType(ThingDef bench, RecipeDef recipe) => DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d.workType != null && d.Worker is WorkGiver_DoBill && d.fixedBillGiverDefs != null && d.fixedBillGiverDefs.Contains(bench)
                && (recipe.requiredGiverWorkType == null || recipe.requiredGiverWorkType == d.workType))
            .OrderBy(d => d.defName, StringComparer.Ordinal).Select(d => d.workType).FirstOrDefault();

        internal static Obs.RecipeState Row(RecipeDef recipe, ThingDef benchDef, Thing? bench)
        {
            var row = new Obs.RecipeState { Recipe = Definition(recipe), AvailableNow = recipe.AvailableNow };
            // workAmount is -1 when the work comes from the product's own
            // WorkToMake (every stuff-made item); resolve it the way the
            // game does, stuff-agnostic, and leave it unset if that fails.
            if (recipe.workAmount >= 0) row.WorkAmount = Number(recipe.workAmount);
            else { try { row.WorkAmount = Number(recipe.WorkAmountTotal(null)); } catch (Exception) { } }
            if (bench != null) row.AvailableOnBench = recipe.AvailableOnNow(bench);
            if (recipe.workSkill != null) row.WorkSkill = Id(recipe.workSkill.defName);
            var work = WorkType(benchDef, recipe);
            if (work != null) row.WorkType = Id(work.defName);
            foreach (var skill in recipe.skillRequirements ?? new List<SkillRequirement>())
                row.Skills.Add(new Obs.SkillRequirement { DefName = Id(skill.skill.defName), Minimum = skill.minLevel });
            Bound(row.Skills.Count);
            foreach (var product in recipe.products ?? new List<ThingDefCountClass>())
                row.Products.Add(new Obs.Quantity { DefName = Id(product.thingDef.defName), Units = product.count });
            Bound(row.Products.Count);
            foreach (var ingredient in recipe.ingredients ?? new List<IngredientCount>())
            {
                var allowed = ingredient.filter.AllowedThingDefs.Where(d => recipe.fixedIngredientFilter == null || recipe.fixedIngredientFilter.Allows(d))
                    .Distinct().OrderBy(d => d.defName, StringComparer.Ordinal).ToList();
                Bound(allowed.Count);
                var slot = new Obs.IngredientRequirement { Required = Number(ingredient.GetBaseCount()), Complete = true };
                foreach (var def in allowed)
                {
                    slot.AllowedDefNames.Add(Id(def.defName));
                    var count = ingredient.CountRequiredOfFor(def, recipe, null);
                    if (count < 0) throw new InvalidOperationException("Negative ingredient count.");
                    slot.Alternatives.Add(new Obs.Quantity { DefName = Id(def.defName), Units = count });
                }
                row.Ingredients.Add(slot);
            }
            Bound(row.Ingredients.Count);
            return row;
        }

        private static Obs.EntityRef Entity(Thing thing) => new Obs.EntityRef { Id = Id(thing.GetUniqueLoadID()), DefName = Id(thing.def.defName),
            Label = PlacementPreviewOperation.Diagnostic(thing.LabelCap), MapId = thing.Map.uniqueID, Position = new Common.Cell { X = thing.Position.x, Z = thing.Position.z } };
        private static Obs.DefinitionRef Definition(Def def)
        {
            var value = new Obs.DefinitionRef { DefName = Id(def.defName) };
            if (def.label != null) value.Label = PlacementPreviewOperation.Diagnostic(def.label);
            return value;
        }
        private static object Encode(IMessage reply)
        {
            if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes) throw new ReadLimit("Reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static double Number(double value) { if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new InvalidOperationException("Invalid recipe quantity."); return value; }
        private static void Bound(int count) { if (count > Limit) throw new ReadLimit("Recipe child collection exceeds 256."); }
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Invalid bill identifier.");
        // The detail names the failure so a controller log is diagnosable
        // without the game log; the full trace still goes to the game log.
        private static string Failed(string what, Exception e)
        {
            Log.Warning("[RimGovernor] " + what + " read failed: " + e);
            return what + " could not be read completely: " + PlacementPreviewOperation.Diagnostic(e.GetType().Name + ": " + e.Message);
        }
        private static Common.Unavailable Missing(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = (ulong)filtered, Unreadable = 0 };
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
    }
}
