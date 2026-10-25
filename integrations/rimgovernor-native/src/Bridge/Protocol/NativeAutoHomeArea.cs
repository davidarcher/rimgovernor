using System;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // AutoHomeAreaIntent on Actions/Apply: the game's home-area
    // auto-expand (Find.PlaySettings.autoHomeArea), a save-level setting. A
    // value that already holds applies again (UNCHANGED). The colony read
    // reports the value (HomeCoverageFacts.AutoHomeArea).
    internal static class NativeAutoHomeArea
    {
        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.AutoHomeAreaIntent? intent) =>
            intent == null
                ? GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Auto home area requires an explicit enabled value.")
                : Find.PlaySettings == null ? GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Play settings are unavailable.") : null;

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.AutoHomeAreaIntent intent)
        {
            var failure = Validate(intent);
            if (failure != null) throw new ApplyRefusedException(failure);
            var settings = Find.PlaySettings;
            var changed = settings.autoHomeArea != intent.Enabled;
            settings.autoHomeArea = intent.Enabled;
            if (settings.autoHomeArea != intent.Enabled) throw new InvalidOperationException("Native auto home area requires readback.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect {
                Fields = [new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.AutoHomeArea,
                    Outcome = changed ? RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied : RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Unchanged }] } };
        }
    }

    internal sealed class AutoHomeAreaActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeAutoHomeArea.Validate(action.Intent?.AutoHomeArea);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeAutoHomeArea.Apply(action.Intent?.AutoHomeArea ?? throw new InvalidOperationException("Auto home area intent missing"));
    }
}
