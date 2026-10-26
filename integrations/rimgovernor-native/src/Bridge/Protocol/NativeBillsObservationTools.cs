using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace RimGovernor.Native
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
        internal static RimGovernor.Brain.GameFacts.Observations.BillsSnapshot HandleReadBills(RimGovernor.Brain.GameOrders.Observations.BillsRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                throw NativeFaults.Refused(error);
            return Read(map, parsed, context);
        }

        // Read is the read on the main thread under a validated identity: the
        // snapshot its tool encodes, and the section the bundle carries. Every
        // bench row is whole or the read fails.
        internal static RimGovernor.Brain.GameFacts.Observations.BillsSnapshot Read(Map map, RimGovernor.Brain.GameOrders.Observations.BillsRequest parsed, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            Ready(map);
            try
            {
                var benches = Benches(map, false);
                if (parsed.BenchId is not null) benches = benches.Where(b => RefIndex.Is(b, parsed.BenchId)).ToList();
                return new RimGovernor.Brain.GameFacts.Observations.BillsSnapshot { Context = context, Benches = [.. benches.Select(bench => Stack(bench, context))] };
            }
            catch (Exception e) { throw ReadFailed("Bench or bill facts", e); }
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static RimGovernor.Brain.GameFacts.Observations.RecipesSnapshot HandleReadRecipes(RimGovernor.Brain.GameOrders.Observations.RecipesRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!GameThread.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                throw NativeFaults.Refused(error);
            Ready(map);
            var bench = Benches(map, true).ById(parsed.BenchId)
                ?? throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "No spawned bench with that id is on the current map."));
            try
            {
                var recipes = (bench.def.AllRecipes ?? new List<RecipeDef>()).Where(r => r != null).OrderBy(r => r.defName, StringComparer.Ordinal);
                return new RimGovernor.Brain.GameFacts.Observations.RecipesSnapshot
                {
                    Snapshot = NativeProductionBills.Snapshot(bench, (IBillGiver)bench, context),
                    BenchDef = Id(bench.def.defName),
                    Recipes = [.. recipes.Select(recipe => NativeProductionBills.RecipeRow(bench, recipe))],
                };
            }
            catch (Exception e) { throw ReadFailed("Bench recipe facts", e); }
        }

        private static void Ready(Map map)
        {
            if (Faction.OfPlayerSilentFail == null || map.listerThings == null)
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NativeComponentMissing, Detail = "Player faction or map things are unavailable." });
        }

        internal static bool Validate(RimGovernor.Brain.GameOrders.Observations.BillsRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Identity, and optional bench id are required.");
            if (request?.Scope?.ExpectedIdentity == null || request.BenchId is not null && !GameThread.IsIdentifier(request.BenchId)) return false;
            return true;
        }

        internal static bool Validate(RimGovernor.Brain.GameOrders.Observations.RecipesRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Identity and a bench id identifier are required.");
            return request?.Scope?.ExpectedIdentity != null && GameThread.IsIdentifier(request.BenchId);
        }

        private static List<Thing> Benches(Map map, bool allFactions)
        {
            var player = Faction.OfPlayer;
            var all = map.listerThings.AllThings;
            return all.Where(t => t is IBillGiver && !(t is Pawn) && t.Spawned && !t.Destroyed && t.def != null
                    && (allFactions || t.Faction == player))
                .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        }

        internal static RimGovernor.Brain.GameFacts.Observations.BillStackRow Stack(Thing bench, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var giver = (IBillGiver)bench;
            var stack = giver.BillStack ?? throw new InvalidOperationException("Bench bill stack unavailable.");
            // RecipeDef defaults workTableSpeedStat to this stat, so it is the
            // speed the game applies to a recipe that names none; the stat
            // parts (outdoors, temperature, ...) are included by GetStatValue.
            // Every thing has the stat, so a non-finite or negative value is a
            // broken read, not an absent one.
            var workSpeed = bench.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor);
            if (float.IsNaN(workSpeed) || float.IsInfinity(workSpeed) || workSpeed < 0f)
                throw new InvalidOperationException("Bench work speed " + workSpeed + " is not a finite non-negative factor.");
            var bills = new List<RimGovernor.Brain.GameFacts.Observations.BillState>();
            for (var index = 0; index < stack.Count; index++) bills.Add(NativeProductionBills.BillRow(stack.Bills[index], index));
            return new RimGovernor.Brain.GameFacts.Observations.BillStackRow
            {
                Snapshot = NativeProductionBills.Snapshot(bench, giver, context),
                Bench = Id(bench.GetUniqueLoadID()),
                BenchDef = Id(bench.def.defName),
                Usable = NativeProductionBills.Usable(bench),
                Bills = [.. bills],
                WorkSpeed = workSpeed,
                Room = bench.GetRoom() is Room room ? NativeRef.Room(room) : null,
            };
        }

        // The work type whose DoBill giver serves this bench definition, so a
        // worker must have it enabled to take the bill; null when no giver
        // serves it or the recipe demands a different giver work type. Only the
        // bill write paths still resolve it, to pick a worker who has it enabled.
        internal static WorkTypeDef? WorkType(ThingDef bench, RecipeDef recipe) => DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d.workType != null && d.Worker is WorkGiver_DoBill && d.fixedBillGiverDefs != null && d.fixedBillGiverDefs.Contains(bench)
                && (recipe.requiredGiverWorkType == null || recipe.requiredGiverWorkType == d.workType))
            .OrderBy(d => d.defName, StringComparer.Ordinal).Select(d => d.workType).FirstOrDefault();

        private static string Id(string value) => GameThread.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        // The detail names the failure so a controller log is diagnosable
        // without the game log; the full trace still goes to the game log.
        private static Exception ReadFailed(string what, Exception e)
        {
            ModLog.Warn("observe", "" + what + " read failed: " + e);
            return NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                Detail = what + " could not be read completely: " + PlacementPreviewOperation.Diagnostic(e.GetType().Name + ": " + e.Message) });
        }
    }
}
