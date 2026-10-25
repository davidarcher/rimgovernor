using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace RimGovernor.Native
{
    // The authenticated host exposes this capability only to its explicit player-control owner.
    // Model/operation dispatch holds no mode-setting capability; direction is a CAS identity, not authentication.
    internal static class NativeAuthorityControlTools
    {

        internal static RimGovernor.Brain.GameFacts.Authority.ControlReply Apply(RimGovernor.Brain.GameOrders.Authority.ControlRequest request)
        {
            RimGovernor.Brain.GameFacts.Common.Identity? identity;
            RimGovernor.Brain.GameOrders.Authority.SetMode? setMode = null;
            RimGovernor.Brain.GameOrders.Authority.Revoke? revoke = null;
            switch (request.Operation)
            {
                case RimGovernor.Brain.GameOrders.Authority.ControlRequestOperation.SetModeCase { Value: var sm }:
                    setMode = sm;
                    if (sm.ExpectedGeneration == 0
                        || (sm.Mode != RimGovernor.Brain.GameFacts.Authority.Mode.Auto && sm.Mode != RimGovernor.Brain.GameFacts.Authority.Mode.Manual)) return Invalid();
                    identity = sm.Identity;
                    break;
                case RimGovernor.Brain.GameOrders.Authority.ControlRequestOperation.RevokeCase { Value: var rv }:
                    revoke = rv;
                    if (rv.ExpectedGeneration == 0
                        || !ExternalReason(rv.Reason, out _)) return Invalid();
                    identity = rv.Identity;
                    break;
                default: return Invalid();
            }
            if (identity is null) return Invalid();
            if (!GameThread.ValidateIdentity(identity, out var context, out var failure))
                return new RimGovernor.Brain.GameFacts.Authority.ControlReply { Outcome = failure };
            bool settingAuto = setMode is not null && setMode.Mode == RimGovernor.Brain.GameFacts.Authority.Mode.Auto;
            if (settingAuto)
                NativeAuthorityHooks.InitializeForCurrentGame();
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var state) || state == null)
                return new RimGovernor.Brain.GameFacts.Authority.ControlReply { Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable,
                    "Native authority hooks are not initialized.") };
            NativeControlResult result;
            if (setMode is not null)
            {
                var mode = setMode.Mode == RimGovernor.Brain.GameFacts.Authority.Mode.Auto ? NativeControlMode.Auto : NativeControlMode.Manual;
                result = state.SetMode(setMode.ExpectedGeneration, mode);
            }
            else
            {
                NativeControlRevocationReason reason;
                ExternalReason((revoke ?? throw new System.InvalidOperationException("Required native value is unavailable: revoke.")).Reason, out reason);
                result = state.Revoke(revoke.ExpectedGeneration, reason);
            }            context = context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(result.Snapshot.Generation) };
            if (!result.Success)
                return new RimGovernor.Brain.GameFacts.Authority.ControlReply { Outcome = Refusal(result.Error, context) };
            if (revoke is not null)
                return new RimGovernor.Brain.GameFacts.Authority.ControlReply { Outcome = new RimGovernor.Brain.GameFacts.Authority.Revoked
                {
                    Context = context,
                    Authority = new RimGovernor.Brain.GameFacts.Authority.InactiveAuthority { Reason = revoke.Reason }
                } };
            if (!settingAuto)
            {
                var projectedInactive = NativeAuthorityTools.Project(result.Snapshot, context);
                return new RimGovernor.Brain.GameFacts.Authority.ControlReply { Outcome = new RimGovernor.Brain.GameFacts.Authority.Revoked
                {
                    Context = context,
                    Authority = projectedInactive.State?.Inactive ?? new RimGovernor.Brain.GameFacts.Authority.InactiveAuthority { Reason = RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual }
                } };
            }
            if (result.Snapshot.Active) NativeAuthorityHooks.EnsureManualPriorities(Current.Game);
            var projected = NativeAuthorityTools.Project(result.Snapshot, context);
            return new RimGovernor.Brain.GameFacts.Authority.ControlReply { Outcome = new RimGovernor.Brain.GameFacts.Authority.Granted
            {
                Context = context, Authority = projected.State?.Active
            } };
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure Refusal(NativeControlError error, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            RimGovernor.Brain.GameFacts.Common.FailureCode code;
            switch (error)
            {
                case NativeControlError.StaleIdentity: code = RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity; break;
                case NativeControlError.StaleGeneration: code = RimGovernor.Brain.GameFacts.Common.FailureCode.StaleGeneration; break;
                case NativeControlError.AuthorityRequired: code = RimGovernor.Brain.GameFacts.Common.FailureCode.AuthorityRequired; break;
                case NativeControlError.GenerationExhausted: code = RimGovernor.Brain.GameFacts.Common.FailureCode.CapacityExhausted; break;
                default: code = RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable; break;
            }
            return new RimGovernor.Brain.GameFacts.Common.Failure { RefusalClass = RimGovernor.Brain.GameFacts.Common.RefusalClass.Unclassified, Code = code, Detail = "Native authority refused: " + error, ObservedContext = context };
        }

        private static RimGovernor.Brain.GameFacts.Authority.ControlReply Invalid() => new RimGovernor.Brain.GameFacts.Authority.ControlReply
        {
            Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Control requires a complete supported operation with valid identity, generation and mode/reason.")
        };

        private static bool ExternalReason(RimGovernor.Brain.GameFacts.Authority.RevocationReason value, out NativeControlRevocationReason reason)
        {
            switch (value)
            {
                case RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual: reason = NativeControlRevocationReason.Manual; return true;
                case RimGovernor.Brain.GameFacts.Authority.RevocationReason.Disconnect: reason = NativeControlRevocationReason.Disconnect; return true;
                case RimGovernor.Brain.GameFacts.Authority.RevocationReason.Shutdown: reason = NativeControlRevocationReason.Shutdown; return true;
                default: reason = NativeControlRevocationReason.None; return false;
            }
        }
    }
}
