#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The mech branch of the production bill write: one
    // Bill_Mech on a Building_MechGestator, the bill the gestator's bill tab
    // queues. A mech recipe is one whose produced thing is a mechanoid race;
    // the planner reads the mech kind and the mechs' bandwidth cost from the
    // definition catalog. Names confirmed by
    // reflection over Krafs.Rimworld.Ref 1.6.4871: Bill_Mech (a
    // Bill_Production), Building_MechGestator, RecipeDef.ProducedThingDef /
    // gestationCycles / mechResurrection / mechanitorOnlyRecipe,
    // RaceProperties.IsMechanoid / AnyPawnKind, Pawn_MechanitorTracker
    // .TotalBandwidth / UsedBandwidth / UsedBandwidthFromGestation /
    // HasBandwidthForBill. Unverified in game: that a vanilla gestation recipe
    // carries the mech race as its single product (the reference assembly
    // has no def data); a gestation recipe that does not resolve to a mech
    // kind fails the census read and the write loudly.
    internal static class NativeMechBills
    {
        // The PawnKindDef name of the mech a gestation recipe makes, null for
        // every other recipe (resurrection included, which produces no kind).
        internal static string? Kind(RecipeDef recipe)
        {
            if (recipe.mechResurrection) return null;
            var race = recipe.ProducedThingDef?.race;
            if (race != null && race.IsMechanoid)
                return race.AnyPawnKind?.defName ?? throw new InvalidOperationException("Mech recipe " + recipe.defName + " produces a mechanoid race with no pawn kind.");
            if (recipe.gestationCycles > 0)
                throw new InvalidOperationException("Gestation recipe " + recipe.defName + " does not produce a mechanoid race.");
            return null;
        }

        internal static bool Handles(Operations.ProductionBillIntent? intent) =>
            intent != null && intent.HasRecipeDef && DefDatabase<RecipeDef>.GetNamedSilentFail(intent.RecipeDef) is RecipeDef recipe && Kind(recipe) != null;

        private static Common.Failure? Resolve(Operations.ProductionBillIntent intent, Common.ObservationContext context, out Building_MechGestator? bench, out RecipeDef? recipe, out Pawn? overseer)
        {
            bench = null; recipe = null; overseer = null;
            var s = intent.Settings;
            if (!NativeProductionBills.Valid(intent) || s.RepeatMode != Operations.RepeatMode.Count || s.RepeatCount != 1 || s.Worker != null || s.Ingredients != null || intent.ReplaceOwnedBill != null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Mech bill requires an exact gestator, a mech recipe and a single-count bill without worker, ingredients or replacement.");
            var map = ProtoBoundary.LoadedMap(context);
            var gestator = map.listerThings.AllThings.FirstOrDefault(x => x.GetUniqueLoadID() == intent.BenchId) as Building_MechGestator;
            var made = DefDatabase<RecipeDef>.GetNamedSilentFail(intent.RecipeDef);
            if (gestator == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Gestator " + intent.BenchId + " is not a loaded mech gestator.");
            bench = gestator; recipe = made;
            if (made == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Recipe " + intent.RecipeDef + " does not exist.");
            // A standing bill of the recipe applies again.
            if (gestator.BillStack.Bills.OfType<Bill_Mech>().Any(b => b.recipe == made && !BillCommon.IsFinished(b))) return null;
            var work = NativeBillsObservationTools.WorkType(gestator.def, made);
            var cost = made.ProducedThingDef?.GetStatValueAbstract(StatDefOf.BandwidthCost) ?? float.NaN;
            var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState && p.workSettings?.Initialized == true).ToList();
            var rules = new ApplyPreconditions(ProductionBillActionHandler.Kind)
                .Require(() => NativeProductionBills.UsableForNewBill(gestator), "gestator is not usable for bills")
                .Require(() => gestator.BillStack.Count < BillStack.MaxCount, "bill stack is full")
                .Require(() => gestator.def.AllRecipes.Contains(made) && made.AvailableNow && made.AvailableOnNow(gestator), "recipe " + intent.RecipeDef + " is not available on the gestator")
                .Require(() => work != null, "recipe " + intent.RecipeDef + " has no work type on " + gestator.def.defName)
                .Require(() => !float.IsNaN(cost) && !float.IsInfinity(cost), "mech race has no bandwidth cost");
            if (rules.Holds)
            {
                var found = colonists.FirstOrDefault(p => MechanitorUtility.IsMechanitor(p) && p.mechanitor != null && NativeProductionBills.Skilled(p, gestator, made, work!)
                    && p.mechanitor.TotalBandwidth - p.mechanitor.UsedBandwidth - p.mechanitor.UsedBandwidthFromGestation >= cost);
                overseer = found;
                rules.Require(() => found != null, "no free colonist mechanitor with the bandwidth (" + cost + ") and the work for " + intent.RecipeDef);
            }
            return rules.Holds ? null : rules.Failure();
        }

        internal static Common.Failure? Validate(Operations.ProductionBillIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.ProductionBillIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var bench, out var recipe, out var overseer);
            if (failure != null) throw new ApplyRefusedException(failure);
            var gestator = bench!; var made = recipe!; var pawn = overseer;
            var standing = gestator.BillStack.Bills.OfType<Bill_Mech>().FirstOrDefault(b => b.recipe == made && !BillCommon.IsFinished(b));
            if (standing == null)
            {
                var bill = made.MakeNewBill(null) as Bill_Mech ?? throw new InvalidOperationException("Mech recipe " + made.defName + " did not make a mech bill.");
                // The game's own bandwidth rule is the last word on the overseer.
                if (pawn == null || !pawn.mechanitor.HasBandwidthForBill(bill))
                    throw new ApplyRefusedException(Common.FailureCode.InvalidRequest, "no mechanitor has bandwidth for " + made.defName);
                bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
                bill.repeatCount = 1;
                bill.suspended = false;
                gestator.BillStack.AddBill(bill);
                standing = bill;
            }
            return new Receipts.EffectEvidence { Bill = new Receipts.BillEffect { Stack = new Receipts.SnapshotEvidence { EntityId = gestator.GetUniqueLoadID() }, Bill = NativeRef.Of(standing.GetUniqueLoadID()), RecipeDef = standing.recipe.defName } };
        }
    }
}
