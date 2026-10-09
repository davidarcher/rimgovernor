#nullable enable
using System;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // AutoHomeAreaIntent on Actions/Apply: the game's home-area
    // auto-expand (Find.PlaySettings.autoHomeArea), a save-level setting. A
    // value that already holds applies again (UNCHANGED). The colony read
    // reports the value (UpkeepFacts.auto_home_area).
    internal static class NativeAutoHomeArea
    {
        internal static Common.Failure? Validate(Operations.AutoHomeAreaIntent? intent) =>
            intent == null || !intent.HasEnabled
                ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Auto home area requires an explicit enabled value.")
                : Find.PlaySettings == null ? ProtoBoundary.Fail(Common.FailureCode.NotFound, "Play settings are unavailable.") : null;

        internal static Receipts.EffectEvidence Apply(Operations.AutoHomeAreaIntent intent)
        {
            var failure = Validate(intent);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var settings = Find.PlaySettings;
            var changed = settings.autoHomeArea != intent.Enabled;
            settings.autoHomeArea = intent.Enabled;
            if (settings.autoHomeArea != intent.Enabled) throw new InvalidOperationException("Native auto home area requires readback.");
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.AutoHomeArea,
                    Outcome = changed ? Receipts.FieldOutcome.Applied : Receipts.FieldOutcome.Unchanged } } } };
        }
    }

    internal sealed class AutoHomeAreaActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeAutoHomeArea.Validate(action.AutoHomeArea);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeAutoHomeArea.Apply(action.AutoHomeArea);
    }
}
