#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // ReadingPolicyIntent on Actions/Apply: the ReadingPolicy
    // labelled name (made when missing) allows exactly the given book
    // ThingDefs and every book effect. PawnSettingsIntent.reading_policy
    // assigns it (NativePawnSettings.cs). A policy that already matches
    // applies again.
    internal static class NativeReadingPolicy
    {
        internal static bool IsBook(ThingDef d) => d.GetCompProperties<CompProperties_Book>() != null;

        internal static IEnumerable<ThingDef> Books() =>
            DefDatabase<ThingDef>.AllDefsListForReading.Where(IsBook).OrderBy(d => d.defName, StringComparer.Ordinal);

        internal static IEnumerable<string> Allowed(ReadingPolicy p) =>
            p.defFilter.AllowedThingDefs.Where(IsBook).Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal);

        internal static Common.Failure? Validate(Operations.ReadingPolicyIntent? intent, Common.ObservationContext context)
        {
            if (intent == null || !intent.HasName || !ProtoBoundary.IsIdentifier(intent.Name) || intent.Name.Length > 80
                || intent.AllowedDefs.Distinct().Count() != intent.AllowedDefs.Count
                || intent.AllowedDefs.Any(d => DefDatabase<ThingDef>.GetNamedSilentFail(d) is not ThingDef def || !IsBook(def)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A reading policy needs a name and distinct book definitions.");
            if (Current.Game.readingPolicyDatabase.AllReadingPolicies.Count(p => p.label == intent.Name) > 1)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "More than one reading policy has this name.");
            return null;
        }

        internal static Receipts.EffectEvidence Apply(Operations.ReadingPolicyIntent intent, Common.ObservationContext context)
        {
            var failure = Validate(intent, context);
            if (failure != null) throw new ApplyRefusedException(failure);
            var db = Current.Game.readingPolicyDatabase;
            var want = intent.AllowedDefs.OrderBy(d => d, StringComparer.Ordinal).ToList();
            var policy = db.AllReadingPolicies.FirstOrDefault(p => p.label == intent.Name);
            var outcome = Receipts.FieldOutcome.Unchanged;
            if (policy == null || !Allowed(policy).SequenceEqual(want))
            {
                if (policy == null) { policy = db.MakeNewReadingPolicy(); policy.label = intent.Name; }
                foreach (var d in Books()) policy.defFilter.SetAllow(d, want.Contains(d.defName));
                policy.effectFilter.SetAllowAll(null);
                if (!Allowed(policy).SequenceEqual(want)) throw new InvalidOperationException("Native reading policy requires readback.");
                outcome = Receipts.FieldOutcome.Applied;
            }
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = policy.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.ReadingPolicy, Outcome = outcome } } } };
        }
    }

    internal sealed class ReadingPolicyActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeReadingPolicy.Validate(action.ReadingPolicy, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeReadingPolicy.Apply(action.ReadingPolicy, context);
    }
}
