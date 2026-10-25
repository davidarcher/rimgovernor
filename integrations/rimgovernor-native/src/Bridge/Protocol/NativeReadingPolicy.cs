using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // ReadingPolicyIntent on Actions/Apply: the ReadingPolicy
    // labelled name (made when missing) allows exactly the given book
    // ThingDefs and every book effect. PawnSettingsIntent.reading_policy
    // assigns it (NativePawnSettings.cs). A policy that already matches
    // applies again.
    internal static class NativeReadingPolicy
    {
        internal static bool IsBook(ThingDef d) => d.GetCompProperties<CompProperties_Book>() != null;

        internal static IEnumerable<string> Allowed(ReadingPolicy p) =>
            p.defFilter.AllowedThingDefs.Where(IsBook).Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal);

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.ReadingPolicyIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            if (intent == null || intent.Name is null || !GameThread.IsIdentifier(intent.Name) || (intent.Name ?? "").Length > 80
                || intent.AllowedDefs.Distinct().Count() != intent.AllowedDefs.Count
                || intent.AllowedDefs.Any(d => DefDatabase<ThingDef>.GetNamedSilentFail(d) is not ThingDef def || !IsBook(def)))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A reading policy needs a name and distinct book definitions.");
            if (Current.Game.readingPolicyDatabase.AllReadingPolicies.Count(p => p.label == (intent.Name ?? "")) > 1)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "More than one reading policy has this name.");
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.ReadingPolicyIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Validate(intent, context);
            if (failure != null) throw new ApplyRefusedException(failure);
            var db = Current.Game.readingPolicyDatabase;
            var want = intent.AllowedDefs.OrderBy(d => d, StringComparer.Ordinal).ToList();
            var policy = db.AllReadingPolicies.FirstOrDefault(p => p.label == (intent.Name ?? ""));
            var outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Unchanged;
            if (policy == null || !Allowed(policy).SequenceEqual(want))
            {
                if (policy == null) { policy = db.MakeNewReadingPolicy(); policy.label = intent.Name; }
                policy.defFilter.SetDisallowAll();
                foreach (var name in want) policy.defFilter.SetAllow(DefDatabase<ThingDef>.GetNamed(name), true);
                policy.effectFilter.SetAllowAll(null);
                if (!Allowed(policy).SequenceEqual(want)) throw new InvalidOperationException("Native reading policy requires readback.");
                outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied;
            }
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect {
                Fields = [new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.ReadingPolicy, Outcome = outcome }] } };
        }
    }

    internal sealed class ReadingPolicyActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeReadingPolicy.Validate(action.Intent?.ReadingPolicy ?? new RimGovernor.Brain.GameOrders.Operations.ReadingPolicyIntent(), context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeReadingPolicy.Apply(action.Intent?.ReadingPolicy ?? new RimGovernor.Brain.GameOrders.Operations.ReadingPolicyIntent(), context);
    }
}
