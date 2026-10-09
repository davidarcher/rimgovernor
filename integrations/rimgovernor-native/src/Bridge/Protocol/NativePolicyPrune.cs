#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // PolicyPruneIntent on Actions/Apply: delete outfit, drug, food
    // or reading policies, or allowed areas, by load id. Vanilla TryDelete
    // refuses while a live pawn holds the policy, so every such pawn first
    // moves onto its own per-pawn policy: the one labelled with its short
    // name, made from the database's defaults when missing. A pawn restricted
    // to a deleted area becomes unrestricted. An id already gone applies
    // again.
    internal static class NativePolicyPrune
    {
        private sealed class Database
        {
            internal string Name = "";
            internal Func<IEnumerable<Policy>> All = () => Enumerable.Empty<Policy>();
            internal Func<Pawn, Policy?> Get = _ => null;
            internal Action<Pawn, Policy> Set = (_, _) => { };
            internal Func<Policy> Make = () => throw new InvalidOperationException();
            internal Func<Policy, AcceptanceReport> Delete = _ => AcceptanceReport.WasRejected;
        }

        private static Database? For(Operations.PolicyDatabase db)
        {
            var game = Current.Game;
            switch (db)
            {
                case Operations.PolicyDatabase.Outfit:
                    return new Database { Name = "outfit", All = () => game.outfitDatabase.AllOutfits, Get = p => p.outfits?.CurrentApparelPolicy,
                        Set = (p, v) => { if (p.outfits != null) p.outfits.CurrentApparelPolicy = (ApparelPolicy)v; },
                        Make = () => game.outfitDatabase.MakeNewOutfit(), Delete = v => game.outfitDatabase.TryDelete((ApparelPolicy)v) };
                case Operations.PolicyDatabase.Drug:
                    return new Database { Name = "drug", All = () => game.drugPolicyDatabase.AllPolicies, Get = p => p.drugs?.CurrentPolicy,
                        Set = (p, v) => { if (p.drugs != null) p.drugs.CurrentPolicy = (DrugPolicy)v; },
                        Make = () => game.drugPolicyDatabase.MakeNewDrugPolicy(), Delete = v => game.drugPolicyDatabase.TryDelete((DrugPolicy)v) };
                case Operations.PolicyDatabase.Food:
                    return new Database { Name = "food", All = () => game.foodRestrictionDatabase.AllFoodRestrictions, Get = p => p.foodRestriction?.CurrentFoodPolicy,
                        Set = (p, v) => { if (p.foodRestriction != null) p.foodRestriction.CurrentFoodPolicy = (FoodPolicy)v; },
                        Make = () => game.foodRestrictionDatabase.MakeNewFoodRestriction(), Delete = v => game.foodRestrictionDatabase.TryDelete((FoodPolicy)v) };
                case Operations.PolicyDatabase.Reading:
                    return new Database { Name = "reading", All = () => game.readingPolicyDatabase.AllReadingPolicies, Get = p => p.reading?.CurrentPolicy,
                        Set = (p, v) => { if (p.reading != null) p.reading.CurrentPolicy = (ReadingPolicy)v; },
                        Make = () => game.readingPolicyDatabase.MakeNewReadingPolicy(), Delete = v => game.readingPolicyDatabase.TryDelete((ReadingPolicy)v) };
                default:
                    return null;
            }
        }

        internal static Common.Failure? Validate(Operations.PolicyPruneIntent? intent, Common.ObservationContext context)
        {
            if (intent == null || !intent.HasDatabase || intent.Database == Operations.PolicyDatabase.Unspecified)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Policy prune requires a database.");
            if (intent.DeleteIds.Count == 0 || intent.DeleteIds.Any(id => !ProtoBoundary.IsIdentifier(id)) || intent.DeleteIds.Distinct().Count() != intent.DeleteIds.Count)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Policy prune requires distinct policy ids.");
            if (intent.Database == Operations.PolicyDatabase.AllowedArea) ProtoBoundary.LoadedMap(context);
            return null;
        }

        // The live pawns vanilla TryDelete checks.
        private static List<Pawn> Holders() =>
            PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive.OrderBy(p => p.thingIDNumber).ToList();

        internal static Receipts.EffectEvidence Apply(Operations.PolicyPruneIntent intent, Common.ObservationContext context)
        {
            var failure = Validate(intent, context);
            if (failure != null) throw new ApplyRefusedException(failure);
            var effect = new Receipts.PolicyPruneEffect();
            var reassigned = new SortedSet<string>(StringComparer.Ordinal);
            if (intent.Database == Operations.PolicyDatabase.AllowedArea)
            {
                effect.Database = "allowed_area";
                var map = ProtoBoundary.LoadedMap(context);
                foreach (var id in intent.DeleteIds)
                {
                    var area = map.areaManager.AllAreas.OfType<Area_Allowed>().ById(id);
                    if (area == null) continue;
                    foreach (var pawn in Holders().Where(p => p.MapHeld == map && p.playerSettings?.AreaRestrictionInPawnCurrentMap == area))
                    {
                        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                        reassigned.Add(pawn.GetUniqueLoadID());
                    }
                    area.Delete();
                    effect.DeletedIds.Add(id);
                }
            }
            else
            {
                var db = For(intent.Database)!;
                effect.Database = db.Name;
                foreach (var id in intent.DeleteIds)
                {
                    var policy = db.All().ById(id);
                    if (policy == null) continue;
                    foreach (var pawn in Holders().Where(p => db.Get(p) == policy))
                    {
                        var name = pawn.LabelShort;
                        var own = db.All().FirstOrDefault(v => v != policy && v.label == name);
                        if (own == null)
                        {
                            own = db.Make();
                            own.label = name;
                        }
                        db.Set(pawn, own);
                        reassigned.Add(pawn.GetUniqueLoadID());
                    }
                    var report = db.Delete(policy);
                    if (!report.Accepted)
                        throw new ApplyRefusedException(Common.FailureCode.NativeFailure, "Policy " + id + " could not be deleted: " + (report.Reason ?? "refused") + ".");
                    effect.DeletedIds.Add(id);
                }
            }
            effect.ReassignedPawnIds.Add(reassigned);
            return new Receipts.EffectEvidence { PolicyPrune = effect };
        }
    }

    internal sealed class PolicyPruneActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativePolicyPrune.Validate(action.PolicyPrune, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativePolicyPrune.Apply(action.PolicyPrune, context);
    }
}
