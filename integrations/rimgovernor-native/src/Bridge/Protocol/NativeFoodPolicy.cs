#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    // A saved whitelist is current state. Native suitability, title and
    // veneration remain legality; health and ingredient checks still run in
    // RimWorld's ordinary food search and ingestion. Never call WillEat here:
    // it includes precisely the saved whitelist this method reconciles.
    internal static class NativeFoodPolicy
    {
        private static FoodPolicy? Current(Pawn pawn) => pawn.foodRestriction?.GetCurrentRespectedRestriction(pawn);
        private static bool Eligible(Pawn pawn, ThingDef def) => def.IsNutritionGivingIngestible
            && !def.IsDrug && !def.IsCorpse && def.ingestible != null
            && (def.ingestible.foodType & FoodTypeFlags.Kibble) == 0
            && pawn.FoodIsSuitable(def) && !FoodUtility.IsVeneratedAnimalMeatOrCorpse(def, pawn)
            && !FoodUtility.InappropriateForTitle(def, pawn, allowIfStarving: true)
            && (!HumanFoodFacts.IsHumanMeat(def) || HumanFoodFacts.AcceptsMeat(pawn));

        internal static Obs.FoodRestriction? Read(Pawn pawn)
        {
            var policy = Current(pawn);
            if (policy == null || pawn.needs?.food == null || pawn.DevelopmentalStage.Baby()) return null;
            var row = new Obs.FoodRestriction { PolicyId = policy.GetUniqueLoadID() };
            row.AllowedDefs.Add(policy.filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal));
            row.EligibleDefs.Add(DefDatabase<ThingDef>.AllDefsListForReading.Where(d => Eligible(pawn, d))
                .Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal));
            return row;
        }

        private static string Hash(Action<BinaryWriter> write)
        {
            using (var bytes = new MemoryStream()) {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true)) write(writer);
                using (var hash = SHA256.Create())
                    return "food-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static string SettingsToken(string work, Pawn pawn) => Hash(w => { w.Write(work); w.Write(Configuration(pawn)); });
        private static string Configuration(Pawn pawn) => Hash(w => {
            var policy = Current(pawn);
            w.Write(policy?.GetUniqueLoadID() ?? "");
            if (policy != null) WriteFilter(w, policy.filter);
        });
        private static void WriteFilter(BinaryWriter w, ThingFilter filter)
        {
            NativeStockpileSettings.WriteSignature(w, filter);
            w.Write(filter.AllowedMentalBreakChance.min); w.Write(filter.AllowedMentalBreakChance.max);
        }
        private static string Signature(ThingFilter filter) => Hash(w => WriteFilter(w, filter));

        internal static bool Valid(Operations.DefinitionList? allow) => allow == null
            || allow.Defs.Count > 0 && allow.Defs.Count <= 256 && allow.Defs.All(ProtoBoundary.IsIdentifier)
                && allow.Defs.Distinct(StringComparer.Ordinal).Count() == allow.Defs.Count;
        internal static bool Writable(Pawn pawn, Operations.DefinitionList? allow) => allow == null
            || Current(pawn) != null && pawn.needs?.food != null && !pawn.DevelopmentalStage.Baby()
                && allow.Defs.All(name => DefDatabase<ThingDef>.GetNamedSilentFail(name) is ThingDef def && Eligible(pawn, def));
        internal static bool Matches(Pawn pawn, Operations.DefinitionList? allow) => allow == null
            || Current(pawn) is FoodPolicy policy && allow.Defs.All(name => policy.filter.Allows(DefDatabase<ThingDef>.GetNamed(name)));

        internal static void Apply(Pawn pawn, Operations.DefinitionList? allow)
        {
            if (allow == null) return;
            if (!Writable(pawn, allow)) throw new InvalidOperationException("Food eligibility changed.");
            var filter = new ThingFilter();
            filter.CopyAllowancesFrom(Current(pawn)!.filter);
            foreach (var name in allow.Defs) filter.SetAllow(DefDatabase<ThingDef>.GetNamed(name), true);
            // Never edit a shared Manual policy. Reuse an identical saved policy
            // when available, so repeated takeovers do not accumulate copies.
            var signature = Signature(filter);
            var database = Verse.Current.Game.foodRestrictionDatabase;
            var desired = database.AllFoodRestrictions.FirstOrDefault(p => Signature(p.filter) == signature);
            if (desired == null) {
                desired = database.MakeNewFoodRestriction();
                desired.label = "Auto food";
                desired.filter.CopyAllowancesFrom(filter);
            }
            pawn.foodRestriction.CurrentFoodPolicy = desired;
        }
    }
}
