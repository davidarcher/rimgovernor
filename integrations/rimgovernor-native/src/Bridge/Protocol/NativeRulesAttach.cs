#nullable enable
using System.Linq;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // RulesAttachIntent on Actions/Apply: the replace-all of rules_attach as a journaled intent. The
    // lease is relative (apply tick + lease_ticks), so a resent key never carries a stale absolute tick. A
    // refused rule refuses the whole action and changes nothing; an empty list clears.
    internal static class NativeRulesAttach
    {
        internal static Common.Failure? Validate(Operations.RulesAttachIntent? intent)
        {
            if (intent == null || !intent.HasLeaseTicks || intent.LeaseTicks <= 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Rules attach requires a positive lease.");
            var refused = new NativeRuleBook().Attach(intent.Rules, 1).Refused;
            if (refused.Count > 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Rules refused: " + string.Join(", ", refused.Select(r => r.RuleId + "=" + r.Reason)));
            return NativeRuleRuntime.EnsureHooks() ? null : ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native rule hooks are unavailable.");
        }

        internal static Receipts.EffectEvidence Apply(Operations.RulesAttachIntent intent, Common.ObservationContext context)
        {
            var failure = Validate(intent);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            NativeRuleRuntime.Attach(context, intent.Rules, context.Tick + intent.LeaseTicks);
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.Rules, Outcome = Receipts.FieldOutcome.Applied } } } };
        }
    }

    internal sealed class RulesAttachActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeRulesAttach.Validate(action.RulesAttach);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeRulesAttach.Apply(action.RulesAttach, context);
    }
}
