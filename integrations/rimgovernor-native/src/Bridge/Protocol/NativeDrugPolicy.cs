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
    // DrugPolicyIntent on Actions/Apply (#1537): the DrugPolicy labelled name
    // (made when missing) carries exactly the given entries and every other
    // drug is off. PawnSettingsIntent.drug_policy assigns it
    // (NativePawnSettings.cs). A policy that already matches applies again.
    internal static class NativeDrugPolicy
    {
        internal static void Install() => SocialBeerBill.Install();

        internal static bool Off(DrugPolicyEntry e) => !e.allowedForJoy && !e.allowedForAddiction && !e.allowScheduled && e.takeToInventory == 0;

        // The entries that allow anything, by drug defName.
        internal static IEnumerable<Operations.DrugPolicyEntry> Entries(DrugPolicy p)
        {
            var rows = new List<Operations.DrugPolicyEntry>();
            for (var i = 0; i < p.Count; i++) {
                var e = p[i];
                if (e?.drug == null || Off(e)) continue;
                rows.Add(new Operations.DrugPolicyEntry { DrugDef = e.drug.defName, AllowedForJoy = e.allowedForJoy, AllowedForAddiction = e.allowedForAddiction,
                    AllowScheduled = e.allowScheduled, DaysFrequency = e.daysFrequency, OnlyIfMoodBelow = e.onlyIfMoodBelow,
                    OnlyIfJoyBelow = e.onlyIfJoyBelow, TakeToInventory = e.takeToInventory });
            }
            return rows.OrderBy(r => r.DrugDef, StringComparer.Ordinal);
        }

        private static bool Matches(DrugPolicy p, Operations.DrugPolicyIntent intent) =>
            Entries(p).SequenceEqual(intent.Entries.OrderBy(r => r.DrugDef, StringComparer.Ordinal));

        private static bool Unit(float v) => !float.IsNaN(v) && v >= 0f && v <= 1f;

        private static bool ValidEntry(Operations.DrugPolicyEntry e) => e.HasDrugDef && ProtoBoundary.IsIdentifier(e.DrugDef)
            && e.HasAllowedForJoy && e.HasAllowedForAddiction && e.HasAllowScheduled && e.HasDaysFrequency && e.HasOnlyIfMoodBelow && e.HasOnlyIfJoyBelow && e.HasTakeToInventory
            && (e.AllowedForJoy || e.AllowedForAddiction || e.AllowScheduled || e.TakeToInventory != 0)
            && !float.IsNaN(e.DaysFrequency) && e.DaysFrequency > 0f && e.DaysFrequency <= 1000f && Unit(e.OnlyIfMoodBelow) && Unit(e.OnlyIfJoyBelow)
            && e.TakeToInventory >= 0 && e.TakeToInventory <= 10;

        internal static Common.Failure? Validate(Operations.DrugPolicyIntent? intent, Common.ObservationContext context)
        {
            if (intent == null || !intent.HasName || !ProtoBoundary.IsIdentifier(intent.Name) || intent.Name.Length > 80
                || !intent.Entries.All(ValidEntry)
                || intent.Entries.Select(e => e.DrugDef).Distinct(StringComparer.Ordinal).Count() != intent.Entries.Count)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A drug policy needs a name and at most one valid entry per drug.");
            var db = Current.Game.drugPolicyDatabase;
            var named = db.AllPolicies.Where(p => p.label == intent.Name).ToList();
            if (named.Count > 1)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "More than one drug policy has this name.");
            // A new policy lists every drug the game has; an existing one its own.
            var policy = named.FirstOrDefault();
            bool Listed(string def) => DefDatabase<ThingDef>.GetNamedSilentFail(def) is ThingDef d && d.IsDrug
                && (policy == null || Enumerable.Range(0, policy.Count).Any(i => policy[i].drug == d));
            if (!intent.Entries.All(e => Listed(e.DrugDef)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Every entry must name a drug the policy lists.");
            return null;
        }

        internal static Receipts.EffectEvidence Apply(Operations.DrugPolicyIntent intent, Common.ObservationContext context)
        {
            var failure = Validate(intent, context);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var db = Current.Game.drugPolicyDatabase;
            var policy = db.AllPolicies.FirstOrDefault(p => p.label == intent.Name);
            var outcome = Receipts.FieldOutcome.Unchanged;
            if (policy == null || !Matches(policy, intent))
            {
                if (policy == null) { policy = db.MakeNewDrugPolicy(); policy.label = intent.Name; }
                var want = intent.Entries.ToDictionary(e => e.DrugDef, StringComparer.Ordinal);
                for (var i = 0; i < policy.Count; i++) {
                    var e = policy[i];
                    if (e?.drug == null) continue;
                    if (want.TryGetValue(e.drug.defName, out var row)) {
                        e.allowedForJoy = row.AllowedForJoy; e.allowedForAddiction = row.AllowedForAddiction; e.allowScheduled = row.AllowScheduled;
                        e.daysFrequency = row.DaysFrequency; e.onlyIfMoodBelow = row.OnlyIfMoodBelow; e.onlyIfJoyBelow = row.OnlyIfJoyBelow;
                        e.takeToInventory = row.TakeToInventory;
                    } else {
                        e.allowedForJoy = false; e.allowedForAddiction = false; e.allowScheduled = false; e.takeToInventory = 0;
                    }
                }
                if (!Matches(policy, intent)) throw new InvalidOperationException("Native drug policy requires readback.");
                outcome = Receipts.FieldOutcome.Applied;
            }
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = policy.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.DrugPolicy, Outcome = outcome } } } };
        }
    }

    internal sealed class DrugPolicyActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeDrugPolicy.Validate(action.DrugPolicy, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeDrugPolicy.Apply(action.DrugPolicy, context);
    }
}
