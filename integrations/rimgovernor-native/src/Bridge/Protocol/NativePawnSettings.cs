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
    // PawnSettingsIntent on Actions/Apply (#1299, epic #1292): one per-pawn
    // Assign-tab setting on one spawned colony pawn. hostility_response is
    // the pawn's playerSettings.hostilityResponse, where the Assign tab
    // offers it (UsesConfigurableHostilityResponse); Attack is refused for a
    // violence-incapable pawn, as the tab refuses it. self_tend (#1305) is
    // playerSettings.selfTend, refused for a pawn that cannot doctor (the
    // tab hides the checkbox). The other arms are refused until their epic
    // issues land. A setting that already holds applies again.
    internal static class NativePawnSettings
    {
        private static Common.Failure? Resolve(Operations.PawnSettingsIntent? intent, Common.ObservationContext context,
            out Pawn? pawn, out HostilityResponseMode mode)
        {
            pawn = null; mode = HostilityResponseMode.Attack;
            if (intent == null || !ProtoBoundary.IsIdentifier(intent.PawnId))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn settings require an exact pawn.");
            var kind = intent.SettingCase;
            if (kind != Operations.PawnSettingsIntent.SettingOneofCase.HostilityResponse && kind != Operations.PawnSettingsIntent.SettingOneofCase.SelfTend)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Only the hostility_response and self_tend settings are supported.");
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.HostilityResponse
                && (!Enum.TryParse(intent.HostilityResponse, false, out mode) || !Enum.IsDefined(typeof(HostilityResponseMode), mode)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Hostility response must be Ignore, Attack or Flee.");
            var id = intent.PawnId;
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == id);
            if (pawn == null || pawn.Dead || pawn.playerSettings == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living colony pawn with player settings is not spawned on this map.");
            if (kind == Operations.PawnSettingsIntent.SettingOneofCase.SelfTend)
                return pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                    ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A pawn that cannot doctor has no self-tend setting.") : null;
            if (!pawn.playerSettings.UsesConfigurableHostilityResponse)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living colony pawn with a configurable hostility response is not spawned on this map.");
            if (mode == HostilityResponseMode.Attack && pawn.WorkTagIsDisabled(WorkTags.Violent))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A violence-incapable pawn cannot be set to Attack.");
            return null;
        }

        internal static Common.Failure? Validate(Operations.PawnSettingsIntent? intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.PawnSettingsIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var mode);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            var settings = pawn!.playerSettings;
            Receipts.FieldResult field;
            if (intent.SettingCase == Operations.PawnSettingsIntent.SettingOneofCase.SelfTend) {
                var want = intent.SelfTend;
                var outcome = settings.selfTend == want ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied;
                settings.selfTend = want;
                if (settings.selfTend != want) throw new InvalidOperationException("Native self-tend requires readback.");
                field = new Receipts.FieldResult { Field = Receipts.SettingsField.SelfTend, Outcome = outcome };
            } else {
                var outcome = settings.hostilityResponse == mode ? Receipts.FieldOutcome.Unchanged : Receipts.FieldOutcome.Applied;
                settings.hostilityResponse = mode;
                if (settings.hostilityResponse != mode) throw new InvalidOperationException("Native hostility response requires readback.");
                field = new Receipts.FieldResult { Field = Receipts.SettingsField.Hostility, Outcome = outcome };
            }
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = pawn.GetUniqueLoadID() },
                Fields = { field } } };
        }
    }

    internal sealed class PawnSettingsActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativePawnSettings.Validate(action.PawnSettings, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativePawnSettings.Apply(action.PawnSettings, context);
    }
}
