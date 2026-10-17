#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Read-only bench census behind Observations/ReadBills and ReadRecipes.
    // Every bench row carries the NativeProductionBills.Snapshot token of its
    // whole ordered bill stack. Pawns also implement IBillGiver (surgery); they are not benches
    // and are excluded here. A recipe read without bench_id is the definition
    // catalog: which player-buildable benches host a recipe and whether its
    // research is complete, so a planner can stage a bench before one exists.
    internal static class NativeBillsObservationTools
    {

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.BillsReply HandleReadBills(Obs.BillsRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) return new Obs.BillsReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                return new Obs.BillsReply { Outcome = error };
            return Read(map, parsed, context);
        }

        // Read is the read on the main thread under a validated identity: the
        // reply its tool encodes, and the section the bundle carries.
        internal static Obs.BillsReply Read(Map map, Obs.BillsRequest parsed, Common.ObservationContext context)
        {
            try
            {
                if (Faction.OfPlayerSilentFail == null || map.listerThings == null)
                    return new Obs.BillsReply { Outcome = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Player faction or map things are unavailable.") };
                var benches = Benches(map, parsed.AllFactions.GetValueOrDefault());
                if (parsed.BenchId is not null) benches = benches.Where(b => RefIndex.Is(b, parsed.BenchId)).ToList();
                var snapshot = new Obs.BillsSnapshot { Context = context };
                foreach (var bench in benches) snapshot = snapshot with { Benches = [.. snapshot.Benches, Stack(bench, map, context)] };
                return new Obs.BillsReply { Outcome = snapshot };
            }
            catch (Exception e) { return new Obs.BillsReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, Failed("Bench or bill facts", e)) }; }
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Obs.RecipesReply HandleReadRecipes(Obs.RecipesRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) return new Obs.RecipesReply { Outcome = invalidRequest };
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                return new Obs.RecipesReply { Outcome = error };
            try
            {
                if (Faction.OfPlayerSilentFail == null || map.listerThings == null)
                    return new Obs.RecipesReply { Outcome = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Player faction or map things are unavailable.") };
                var bench = Benches(map, true).ById(parsed.BenchId);
                if (bench == null)
                    return new Obs.RecipesReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No spawned bench with that id is on the current map.") };
                var recipes = (bench.def.AllRecipes ?? new List<RecipeDef>()).Where(r => r != null).OrderBy(r => r.defName, StringComparer.Ordinal).ToList();
                var snapshot = new Obs.RecipesSnapshot { Context = context, Snapshot = NativeProductionBills.Snapshot(bench, (IBillGiver)bench, context), BenchDef = Id(bench.def.defName) };
                foreach (var recipe in recipes) snapshot = snapshot with { Recipes = [.. snapshot.Recipes, Recipe(bench, recipe)] };
                return new Obs.RecipesReply { Outcome = snapshot };
            }
            catch (Exception e) { return new Obs.RecipesReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, Failed("Bench recipe facts", e)) }; }
        }

        internal static bool Validate(Obs.BillsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, and optional bench id are required.");
            if (request?.Scope?.ExpectedIdentity == null || request.BenchId is not null && !ProtoBoundary.IsIdentifier(request.BenchId)) return false;
            return true;
        }

        internal static bool Validate(Obs.RecipesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity and a bench id identifier are required.");
            return request?.Scope?.ExpectedIdentity != null && request.BenchId is not null && ProtoBoundary.IsIdentifier(request.BenchId);
        }

        private static List<Thing> Benches(Map map, bool allFactions)
        {
            var player = Faction.OfPlayer;
            var all = map.listerThings.AllThings;
            return all.Where(t => t is IBillGiver && !(t is Pawn) && t.Spawned && !t.Destroyed && t.def != null
                    && (allFactions || t.Faction == player))
                .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        }

        private static Obs.BillStackRow Stack(Thing bench, Map map, Common.ObservationContext context)
        {
            var giver = (IBillGiver)bench;
            var stack = giver.BillStack ?? throw new InvalidOperationException("Bench bill stack unavailable.");
            var usable = NativeProductionBills.Usable(bench);
            var row = new Obs.BillStackRow { Snapshot = NativeProductionBills.Snapshot(bench, giver, context), Bench = NativeBuildingObservationTools.Ref(bench),
                Usable = usable };
            // RecipeDef defaults workTableSpeedStat to this stat, so it is the
            // speed the game applies to a recipe that names none; the stat
            // parts (outdoors, temperature, ...) are included by GetStatValue.
            var workSpeed = bench.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor);
            if (!float.IsNaN(workSpeed) && !float.IsInfinity(workSpeed) && workSpeed >= 0f) row = row with { WorkSpeed = workSpeed };
            var bills = new List<Obs.BillState>();
            for (var index = 0; index < stack.Count; index++) bills.Add(NativeProductionBills.BillRow(stack.Bills[index], index));
            return row with { Bills = [.. bills] };
        }

        // Only what a frame alone knows: whether the game offers the recipe now
        // and on this bench. The rest is the recipe's catalog row.
        private static Obs.RecipeState Recipe(Thing bench, RecipeDef recipe)
            => new Obs.RecipeState { Recipe = new Obs.DefinitionRef { DefName = Id(recipe.defName) },
                AvailableNow = recipe.AvailableNow, AvailableOnBench = recipe.AvailableOnNow(bench) };

        // The work type whose DoBill giver serves this bench definition, so a
        // worker must have it enabled to take the bill; null when no giver
        // serves it or the recipe demands a different giver work type. Only the
        // bill write paths still resolve it, to pick a worker who has it enabled.
        internal static WorkTypeDef? WorkType(ThingDef bench, RecipeDef recipe) => DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d.workType != null && d.Worker is WorkGiver_DoBill && d.fixedBillGiverDefs != null && d.fixedBillGiverDefs.Contains(bench)
                && (recipe.requiredGiverWorkType == null || recipe.requiredGiverWorkType == d.workType))
            .OrderBy(d => d.defName, StringComparer.Ordinal).Select(d => d.workType).FirstOrDefault();

        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        // The detail names the failure so a controller log is diagnosable
        // without the game log; the full trace still goes to the game log.
        private static string Failed(string what, Exception e)
        {
            ModLog.Warn("observe", "" + what + " read failed: " + e);
            return what + " could not be read completely: " + PlacementPreviewOperation.Diagnostic(e.GetType().Name + ": " + e.Message);
        }
    }
}
