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
    // Read-only bench census behind Observations/ReadBills and ReadRecipes.
    // Every bench row carries the same NativeProductionBills.Snapshot token
    // Operations/Execute AddBill checks, so a caller can read then dispatch in
    // one step. Pawns also implement IBillGiver (surgery); they are not benches
    // and are excluded here. A recipe read without bench_id is the definition
    // catalog: which player-buildable benches host a recipe and whether its
    // research is complete, so a planner can stage a bench before one exists.
    public sealed class NativeBillsObservationTools
    {
        internal const string BillsToolName = "rimgovernor/observations_read_bills";
        internal const string RecipesToolName = "rimgovernor/observations_read_recipes";
        private const int MaxRows = 256;

        [Tool(BillsToolName, Title = "Read typed bill census", Description = "Complete bounded bill stacks of every spawned bench (IBillGiver building) on the current map, with the CAS snapshot token AddBill checks. Defaults to player benches; no bill changes.")]
        [ToolResponse("payload", "string", "Official ProtoJSON BillsReply.", Always = true)]
        public async Task<object> ReadBills(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON BillsRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, BillsToolName, request, Obs.BillsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.BillsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.BillsReply { Failure = error });
                try
                {
                    if (Faction.OfPlayerSilentFail == null || map.listerThings == null)
                        return ProtoBoundary.Encode(new Obs.BillsReply { Unavailable = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Player faction or map things are unavailable.") });
                    var benches = Benches(map, parsed.AllFactions);
                    if (parsed.HasBenchId) benches = benches.Where(b => b.GetUniqueLoadID() == parsed.BenchId).ToList();
                    var seed = "bills" + parsed.AllFactions + (parsed.HasBenchId ? parsed.BenchId : "");
                    var afterCursor = benches;
                    if (parsed.Page != null && parsed.Page.HasCursor && parsed.Page.Cursor.Length != 0)
                    {
                        if (!NativeObservationSnapshot.Cursor.TryDecode(context.Identity, seed, parsed.Page.Cursor, out var after))
                            return ProtoBoundary.Encode(new Obs.BillsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, "Bills cursor is stale or does not match this query.") });
                        afterCursor = benches.Where(b => string.CompareOrdinal(b.GetUniqueLoadID(), after) > 0).ToList();
                    }
                    var limit = parsed.Page?.HasLimit == true ? (int)parsed.Page.Limit : MaxRows;
                    var page = afterCursor.Take(limit).ToList();
                    var truncated = afterCursor.Count > page.Count;
                    var completeness = Complete(page.Count);
                    completeness.Matched = (ulong)benches.Count;
                    completeness.Page.Complete = !truncated;
                    if (truncated) completeness.Page.NextCursor = NativeObservationSnapshot.Cursor.Encode(context.Identity, seed, page[page.Count - 1].GetUniqueLoadID());
                    var snapshot = new Obs.BillsSnapshot { Context = context, Completeness = completeness };
                    foreach (var bench in page) snapshot.Benches.Add(Stack(bench, map, context));
                    return Encode(new Obs.BillsReply { Observed = snapshot });
                }
                catch (ReadLimit limit) { return ProtoBoundary.Encode(new Obs.BillsReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, limit.Message) }); }
                catch (Exception e) { return ProtoBoundary.Encode(new Obs.BillsReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, Failed("Bench or bill facts", e)) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool(RecipesToolName, Title = "Read typed bench recipes", Description = "With bench_id: complete bounded recipe catalog of one bench: availability, work, the work type a worker must enable, skill requirements, per-slot required ingredient counts and products. Without: the recipe definition catalog with hosting player-buildable bench definitions, optionally narrowed to recipes producing product_def. No stock scan; ingredient rows carry required amounts only.")]
        [ToolResponse("payload", "string", "Official ProtoJSON RecipesReply.", Always = true)]
        public async Task<object> ReadRecipes(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RecipesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, RecipesToolName, request, Obs.RecipesRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.RecipesReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.RecipesReply { Failure = error });
                try
                {
                    if (Faction.OfPlayerSilentFail == null || map.listerThings == null)
                        return ProtoBoundary.Encode(new Obs.RecipesReply { Unavailable = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Player faction or map things are unavailable.") });
                    var limit = parsed.Page?.HasLimit == true ? (int)parsed.Page.Limit : MaxRows;
                    if (!parsed.HasBenchId)
                    {
                        var catalog = DefDatabase<RecipeDef>.AllDefsListForReading
                            .Where(r => r.products != null && r.products.Count > 0 && (!parsed.HasProductDef || r.products.Any(p => p.thingDef?.defName == parsed.ProductDef)))
                            .Where(r => Hosts(r).Count > 0).OrderBy(r => r.defName, StringComparer.Ordinal).ToList();
                        Require(catalog.Count <= limit, "Recipe catalog exceeds the page limit; narrow with product_def.");
                        var definitions = new Obs.RecipesSnapshot { Context = context, Snapshot = new Obs.SnapshotRef { Context = context.Clone() }, Completeness = Complete(catalog.Count) };
                        foreach (var recipe in catalog)
                        {
                            var hosts = Hosts(recipe);
                            var row = Recipe(hosts[0], null, recipe);
                            foreach (var host in hosts) row.BenchDefs.Add(Id(host.defName));
                            definitions.Recipes.Add(row);
                        }
                        return Encode(new Obs.RecipesReply { Observed = definitions });
                    }
                    var bench = Benches(map, true).FirstOrDefault(b => b.GetUniqueLoadID() == parsed.BenchId);
                    if (bench == null)
                        return ProtoBoundary.Encode(new Obs.RecipesReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No spawned bench with that id is on the current map.") });
                    var recipes = (bench.def.AllRecipes ?? new List<RecipeDef>()).Where(r => r != null).OrderBy(r => r.defName, StringComparer.Ordinal).ToList();
                    Require(recipes.Count <= limit, "Bench recipe catalog exceeds the page limit; the catalog is never sampled.");
                    var snapshot = new Obs.RecipesSnapshot { Context = context, Snapshot = NativeProductionBills.Snapshot(bench, (IBillGiver)bench, context), Completeness = Complete(recipes.Count) };
                    foreach (var recipe in recipes) snapshot.Recipes.Add(Recipe(bench.def, bench, recipe));
                    return Encode(new Obs.RecipesReply { Observed = snapshot });
                }
                catch (ReadLimit limit) { return ProtoBoundary.Encode(new Obs.RecipesReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, limit.Message) }); }
                catch (Exception e) { return ProtoBoundary.Encode(new Obs.RecipesReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, Failed("Bench recipe facts", e)) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.BillsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, optional bench id and page limit 1..256 are required.");
            return request?.Scope?.ExpectedIdentity != null && Page(request.Page)
                && (!request.HasBenchId || ProtoBoundary.IsIdentifier(request.BenchId));
        }

        internal static bool Validate(Obs.RecipesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity and page limit 1..256 are required; bench id and product def must be identifiers.");
            return request?.Scope?.ExpectedIdentity != null && Page(request.Page)
                && (!request.HasBenchId || ProtoBoundary.IsIdentifier(request.BenchId))
                && (!request.HasProductDef || ProtoBoundary.IsIdentifier(request.ProductDef));
        }

        private static bool Page(Common.PageRequest? page) => page == null
            || (!page.HasLimit || page.Limit >= 1 && page.Limit <= MaxRows) && (!page.HasCursor || page.Cursor.Length <= 4096);

        private static List<Thing> Benches(Map map, bool allFactions)
        {
            var player = Faction.OfPlayer;
            var all = map.listerThings.AllThings;
            Require(all.Count <= 65536, "Map thing traversal exceeds 65536 things.");
            return all.Where(t => t is IBillGiver && !(t is Pawn) && t.Spawned && !t.Destroyed && t.def != null
                    && (allFactions || t.Faction == player))
                .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        }

        private static Obs.BillStack Stack(Thing bench, Map map, Common.ObservationContext context)
        {
            var giver = (IBillGiver)bench;
            var stack = giver.BillStack ?? throw new InvalidOperationException("Bench bill stack unavailable.");
            Require(stack.Count <= 15, "Bill stack exceeds BillStack.MaxCount.");
            var row = new Obs.BillStack { Snapshot = NativeProductionBills.Snapshot(bench, giver, context), Bench = Entity(bench, map),
                Usable = NativeProductionBills.Usable(bench), Capacity = 15, Completeness = Complete(stack.Count) };
            if (!row.Usable) row.UnusableReason = bench.Faction != Faction.OfPlayer ? "not player owned"
                : bench.IsForbidden(Faction.OfPlayer) ? "forbidden" : bench.IsBurning() ? "burning" : !giver.CurrentlyUsableForBills() ? "not currently usable for bills" : "unusable";
            for (var index = 0; index < stack.Count; index++) row.Bills.Add(NativeProductionBills.BillRow(stack.Bills[index], index));
            return row;
        }

        // Player-buildable building definitions that host the recipe.
        internal static List<ThingDef> Hosts(RecipeDef recipe) => (recipe.AllRecipeUsers ?? Enumerable.Empty<ThingDef>())
            .Where(d => d.category == ThingCategory.Building && d.BuildableByPlayer && typeof(IBillGiver).IsAssignableFrom(d.thingClass))
            .Distinct().OrderBy(d => d.defName, StringComparer.Ordinal).ToList();

        // The work type whose DoBill giver serves this bench definition, so a
        // worker must have it enabled to take the bill; null when no giver
        // serves it or the recipe demands a different giver work type.
        internal static WorkTypeDef? WorkType(ThingDef bench, RecipeDef recipe) => DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d.workType != null && d.Worker is WorkGiver_DoBill && d.fixedBillGiverDefs != null && d.fixedBillGiverDefs.Contains(bench)
                && (recipe.requiredGiverWorkType == null || recipe.requiredGiverWorkType == d.workType))
            .OrderBy(d => d.defName, StringComparer.Ordinal).Select(d => d.workType).FirstOrDefault();

        private static Obs.RecipeState Recipe(ThingDef benchDef, Thing? bench, RecipeDef recipe)
        {
            var row = new Obs.RecipeState { Recipe = new Obs.DefinitionRef { DefName = Id(recipe.defName) }, AvailableNow = recipe.AvailableNow };
            if (bench != null) row.AvailableOnBench = recipe.AvailableOnNow(bench);
            row.Recipe.Label = PlacementPreviewOperation.Diagnostic(recipe.LabelCap);
            // workAmount is -1 when the work comes from the product's own
            // WorkToMake (every stuff-made item); WorkAmountTotal resolves it
            // the way the game does but throws for multi-product recipes, so
            // leave the amount unset rather than fail the whole read.
            if (recipe.workAmount >= 0) row.WorkAmount = recipe.workAmount;
            else { try { row.WorkAmount = recipe.WorkAmountTotal(null); } catch (Exception) { } }
            if (recipe.workSkill != null) row.WorkSkill = Id(recipe.workSkill.defName);
            var work = WorkType(benchDef, recipe);
            if (work != null) row.WorkType = Id(work.defName);
            var skills = recipe.skillRequirements ?? new List<SkillRequirement>();
            Require(skills.Count <= MaxRows, "Recipe skill requirements exceed bound.");
            foreach (var skill in skills.Where(s => s?.skill != null))
                row.Skills.Add(new Obs.SkillRequirement { DefName = Id(skill.skill.defName), Minimum = skill.minLevel });
            var products = recipe.products ?? new List<ThingDefCountClass>();
            Require(products.Count <= MaxRows, "Recipe products exceed bound.");
            foreach (var product in products.Where(p => p?.thingDef != null))
                row.Products.Add(new Obs.Quantity { DefName = Id(product.thingDef.defName), Units = product.count });
            var ingredients = recipe.ingredients ?? new List<IngredientCount>();
            Require(ingredients.Count <= MaxRows, "Recipe ingredients exceed bound.");
            foreach (var ingredient in ingredients) row.Ingredients.Add(Ingredient(recipe, ingredient));
            return row;
        }

        // Required is the whole count of the slot's single allowed definition;
        // a slot admitting several definitions reports the base count and every
        // allowed name, since the needed count can differ per chosen material.
        // No stock scan: Available/Missing stay unset with a NotRequested issue.
        private static Obs.IngredientRequirement Ingredient(RecipeDef recipe, IngredientCount ingredient)
        {
            var row = new Obs.IngredientRequirement();
            var allowed = (ingredient?.filter?.AllowedThingDefs ?? Enumerable.Empty<ThingDef>()).Where(d => d != null)
                .Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Require(allowed.Count <= 4096, "Ingredient filter exceeds bound.");
            foreach (var name in allowed) row.AllowedDefNames.Add(Id(name));
            if (ingredient == null) { row.Complete = false; row.Issues.Add(Issue("required", Common.UnavailableReason.ReadFailed, "Ingredient slot is unreadable.")); return row; }
            var required = allowed.Count == 1
                ? (double)ingredient.CountRequiredOfFor(DefDatabase<ThingDef>.GetNamed(allowed[0]), recipe, null)
                : Math.Ceiling(ingredient.GetBaseCount());
            row.Required = required;
            // Per-material counts so a slot admitting several materials still
            // funds exactly (RecipeState.ingredients[].alternatives).
            foreach (var name in allowed)
            {
                var count = ingredient.CountRequiredOfFor(DefDatabase<ThingDef>.GetNamed(name), recipe, null);
                if (count < 0) throw new InvalidOperationException("Negative ingredient count.");
                row.Alternatives.Add(new Obs.Quantity { DefName = name, Units = count });
            }
            row.Complete = required >= 0 && !double.IsNaN(required) && !double.IsInfinity(required);
            row.Issues.Add(Issue("available", Common.UnavailableReason.NotRequested, "Recipe catalog reads carry no stock scan."));
            return row;
        }

        private static Obs.EntityRef Entity(Thing thing, Map map) => new Obs.EntityRef { Id = Id(thing.GetUniqueLoadID()), DefName = Id(thing.def.defName),
            Label = PlacementPreviewOperation.Diagnostic(thing.LabelCap), MapId = map.uniqueID, Position = new Common.Cell { X = thing.Position.x, Z = thing.Position.z } };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private static Obs.Completeness Complete(int count) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };
        private static object Encode(IMessage reply)
        {
            Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Complete reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new ReadLimit(message); }
        // The detail names the failure so a controller log is diagnosable
        // without the game log; the full trace still goes to the game log.
        private static string Failed(string what, Exception e)
        {
            Log.Warning("[RimGovernor] " + what + " read failed: " + e);
            return what + " could not be read completely: " + PlacementPreviewOperation.Diagnostic(e.GetType().Name + ": " + e.Message);
        }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
