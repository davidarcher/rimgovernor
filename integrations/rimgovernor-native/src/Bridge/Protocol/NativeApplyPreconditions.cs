#nullable enable
using System;
using Common = RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    // Apply-time preconditions for routine writes (#242). Execute runs on the
    // main thread and applies its effect in the same call, so the rules a
    // Prepare evaluates here hold at the tick the write lands; nothing ticks
    // between the check and the effect. Rules run in order and the first one
    // that fails names the refusal, so a world that moved under the order
    // reports the fact that moved. The snapshot-token comparison is always
    // the last rule: it still refuses a changed world the rules did not
    // catch, and phase 3 of #240 can drop it without touching the rules.
    //
    // A rule that throws counts as failed with its own reason: the object it
    // read is in a state the rule did not anticipate, which is not a state
    // the write may apply to. Rules after a failure never run, so a later
    // rule may dereference what an earlier rule established.
    internal sealed class ApplyPreconditions
    {
        private readonly string kind;
        private string? refused;
        private Common.FailureCode code = Common.FailureCode.InvalidRequest;

        internal ApplyPreconditions(string kind) { this.kind = kind; }

        internal bool Holds => refused == null;
        internal string? Reason => refused;

        internal ApplyPreconditions Require(Func<bool> rule, string reason) => Require(rule, reason, Common.FailureCode.InvalidRequest);

        // Present is the rule for the exact target still being there; it
        // refuses NotFound so a caller that distinguishes a vanished target
        // from a target in the wrong state can.
        internal ApplyPreconditions Present(Func<bool> rule, string reason) => Require(rule, reason, Common.FailureCode.NotFound);

        // Token is the closing CAS rule: the snapshot the controller read
        // must still hash to the token it sent. It keeps the InvalidRequest
        // code the routine kinds always answered a stale token with.
        internal ApplyPreconditions Token(Func<bool> rule, string reason) => Require(rule, reason, Common.FailureCode.InvalidRequest);

        private ApplyPreconditions Require(Func<bool> rule, string reason, Common.FailureCode failureCode)
        {
            if (refused != null) return this;
            bool holds;
            try { holds = rule(); }
            catch (Exception) { holds = false; }
            if (!holds) { refused = reason; code = failureCode; }
            return this;
        }

        // Failure is the wire refusal: "<kind> refused: <reason>". The
        // reason strings are the contract (action-contracts.md); the
        // controller's flight recorder keeps them verbatim.
        internal Common.Failure Failure() => ProtoBoundary.Fail(code, Detail(kind, refused ?? "precondition failed"));

        internal static string Detail(string kind, string reason) => kind + " refused: " + reason;
    }
}
