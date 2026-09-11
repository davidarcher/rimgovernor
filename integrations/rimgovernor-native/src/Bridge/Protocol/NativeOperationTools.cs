using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeOperationState
    {
        private static readonly ConditionalWeakTable<Game, NativeOperationState> States = new ConditionalWeakTable<Game, NativeOperationState>();
        private readonly string colony;
        private readonly string load;
        internal readonly NativeAttemptLedger Ledger;
        internal readonly Dictionary<Common.AttemptKey, NativeConstructionRecord> Construction = new Dictionary<Common.AttemptKey, NativeConstructionRecord>();
        private NativeOperationState(Common.Identity identity)
        { colony = identity.ColonyId; load = identity.LoadToken; Ledger = new NativeAttemptLedger(identity); }
        internal static bool TryGet(Common.Identity identity, out NativeOperationState state)
        {
            state = null;
            return Current.Game != null && States.TryGetValue(Current.Game, out state)
                && state.colony == identity.ColonyId && state.load == identity.LoadToken;
        }
        internal static NativeOperationState ForAdmission(Common.Identity identity)
        {
            NativeOperationState state;
            if (TryGet(identity, out state)) return state;
            States.Remove(Current.Game);
            state = new NativeOperationState(identity); States.Add(Current.Game, state); return state;
        }
    }

    public sealed class NativeOperationTools
    {
        public NativeOperationTools() { NativeConstructionTracking.Install(); }

        [Tool("rimgovernor/operations_execute", Title = "Execute guarded native operation", Description = "Admit one typed construction attempt under current native authority. Exact retries return their original receipt.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ExecuteReply.", Always = true)]
        public async Task<object> Execute(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations ExecuteRequest ProtoJSON string.")] object request = null)
        {
            Operations.ExecuteRequest parsed; Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_execute", request, Operations.ExecuteRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Operations.ExecuteReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => ProtoBoundary.Encode(ExecuteNative(parsed)), cancellationToken).ConfigureAwait(false);
        }

        internal static Operations.ExecuteReply ExecuteNative(Operations.ExecuteRequest request)
        {
            var precondition = request.Precondition;
            if (precondition == null || !precondition.HasExpectedGeneration || precondition.ExpectedGeneration == 0
                || !precondition.HasLeaseId || !ProtoBoundary.IsIdentifier(precondition.LeaseId) || !ValidAttempt(precondition.Attempt))
                return Refuse(Common.FailureCode.InvalidRequest, "A complete authority precondition and positive attempt are required.");
            Common.ObservationContext context; Common.Failure failure;
            if (!ProtoBoundary.ValidateIdentity(precondition.Identity, Find.CurrentMap, out context, out failure))
                return new Operations.ExecuteReply { Failure = failure };
            if (request.Operation == null || request.Operation.CommandCase == Operations.Operation.CommandOneofCase.None)
                return Refuse(Common.FailureCode.InvalidRequest, "An operation is required.");
            var state = NativeOperationState.ForAdmission(context.Identity);
            var prior = state.Ledger.Inspect("rimgovernor.operations.v1.Operations/Execute", request);
            if (prior.Kind != NativeAttemptLedger.DecisionKind.New) return prior.Reply;
            if (request.Operation.CommandCase != Operations.Operation.CommandOneofCase.PlaceBuilding)
                return Refuse(Common.FailureCode.Unsupported, "This native adapter implements PlaceBuilding.");
            if (!NativeConstructionTracking.Ready)
                return Refuse(Common.FailureCode.Unavailable, "Construction transition tracking is unavailable.");
            NativeControlAuthority authority;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out authority) || authority == null)
                return Refuse(Common.FailureCode.AuthorityRequired, "Native authority has not been acquired.");
            var guard = authority.Check(precondition.ExpectedGeneration, precondition.LeaseId, precondition.Attempt.ControllerSessionId);
            context.NativeGeneration = guard.Snapshot.Generation;
            if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
            NativeConstructionPlan plan; RimGovernor.Protocol.Placement.PlacementEvaluated preview;
            try
            {
                if (!NativeConstructionPlan.Prepare(Find.CurrentMap, request.Operation.PlaceBuilding.Placement, context, out plan, out preview, out failure))
                    return new Operations.ExecuteReply { Failure = failure };
            }
            catch (Exception error) { return Refuse(Common.FailureCode.NativeFailure, "Construction validation failed: " + error.GetType().Name); }
            guard = authority.Check(precondition.ExpectedGeneration, precondition.LeaseId, precondition.Attempt.ControllerSessionId);
            context.NativeGeneration = guard.Snapshot.Generation;
            if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
            var owner = new Authority.Owner { ControllerSessionId = guard.Snapshot.Lease.ControllerSessionId,
                PlayerDirection = guard.Snapshot.Lease.PlayerDirection };
            var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context, owner);
            if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply;
            var observed = plan.Proposed();
            try
            {
                using (authority.Owned())
                {
                    var placed = plan.Place(observed);
                    if (placed == null) throw new InvalidOperationException("Native placement returned no object.");
                    var record = NativeConstructionTracking.Register(plan, placed, observed);
                    state.Construction.Add(precondition.Attempt.Clone(), record);
                    if (!record.Matches(placed)) throw new InvalidOperationException("Placed object did not match admitted construction.");
                    return new Operations.ExecuteReply { Receipt = state.Ledger.FinishApplied(admission.Handle,
                        new Receipts.EffectEvidence { Construction = record.Effect }) };
                }
            }
            catch (Exception error)
            {
                NativeConstructionRecord record;
                var lastObserved = state.Construction.TryGetValue(precondition.Attempt, out record)
                    ? new Receipts.EffectEvidence { Construction = record.Effect.Clone() }
                    : observed.CancelledFrameIds.Count > 0 || observed.WipedThingIds.Count > 0
                        ? new Receipts.EffectEvidence { Construction = observed } : null;
                return new Operations.ExecuteReply { Receipt = state.Ledger.FinishUncertain(admission.Handle,
                    lastObserved, "Admitted construction requires observation: " + error.GetType().Name) };
            }
        }

        [Tool("rimgovernor/operations_preview", Title = "Preview typed operation", Description = "Read ordinary construction eligibility without authority or effects.")]
        [ToolResponse("payload", "string", "Official ProtoJSON PreviewReply.", Always = true)]
        public async Task<object> Preview(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations PreviewRequest ProtoJSON string.")] object request = null)
        {
            Operations.PreviewRequest parsed; Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_preview", request, Operations.PreviewRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                Common.ObservationContext context; Common.Failure invalid;
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, Find.CurrentMap, out context, out invalid))
                    return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = invalid });
                if (parsed.Operation == null || parsed.Operation.CommandCase != Operations.Operation.CommandOneofCase.PlaceBuilding)
                    return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Preview implements PlaceBuilding.") });
                NativeConstructionPlan plan; RimGovernor.Protocol.Placement.PlacementEvaluated preview;
                var accepted = NativeConstructionPlan.Prepare(Find.CurrentMap, parsed.Operation.PlaceBuilding.Placement, context, out plan, out preview, out invalid);
                if (preview == null) return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = invalid });
                return ProtoBoundary.Encode(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation
                {
                    Context = context, Accepted = accepted, Reason = accepted ? "" : invalid.Detail, Placement = preview
                } });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/receipts_lookup", Title = "Look up admitted attempt", Description = "Read original admitted receipt without requiring current authority.")]
        [ToolResponse("payload", "string", "Official ProtoJSON LookupReply.", Always = true)]
        public async Task<object> Lookup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official receipts LookupRequest ProtoJSON string.")] object request = null)
        {
            Receipts.LookupRequest parsed; Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/receipts_lookup", request, Receipts.LookupRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Receipts.LookupReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                Common.ObservationContext context; Common.Failure invalid;
                if (!ValidAttempt(parsed.Attempt)) return ProtoBoundary.Encode(new Receipts.LookupReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A complete attempt is required.") });
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, Find.CurrentMap, out context, out invalid))
                    return ProtoBoundary.Encode(new Receipts.LookupReply { Failure = invalid });
                NativeOperationState state;
                return ProtoBoundary.Encode(NativeOperationState.TryGet(context.Identity, out state)
                    ? state.Ledger.Lookup(parsed.Attempt, context)
                    : new Receipts.LookupReply { Unknown = new Receipts.UnknownAttempt { Context = context } });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/receipts_observe_progress", Title = "Observe admitted construction", Description = "Read causally tracked native construction transitions; absence of an attempt never proves completion.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ProgressReply.", Always = true)]
        public async Task<object> ObserveProgress(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official receipts ProgressRequest ProtoJSON string.")] object request = null)
        {
            Receipts.ProgressRequest parsed; Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/receipts_observe_progress", request, Receipts.ProgressRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Receipts.ProgressReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() =>
            {
                Common.ObservationContext context; Common.Failure invalid;
                if (!ValidAttempt(parsed.Attempt)) return ProtoBoundary.Encode(new Receipts.ProgressReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A complete attempt is required.") });
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, Find.CurrentMap, out context, out invalid))
                    return ProtoBoundary.Encode(new Receipts.ProgressReply { Failure = invalid });
                NativeOperationState state; NativeConstructionRecord record;
                if (NativeOperationState.TryGet(context.Identity, out state))
                {
                    var lookup = state.Ledger.Lookup(parsed.Attempt, context);
                    if (lookup.Failure != null) return ProtoBoundary.Encode(new Receipts.ProgressReply { Failure = lookup.Failure });
                }
                var progress = NativeOperationState.TryGet(context.Identity, out state) && state.Construction.TryGetValue(parsed.Attempt, out record)
                    ? record.Observe(parsed.Attempt, context)
                    : new Receipts.Progress { Attempt = parsed.Attempt.Clone(), Context = context, CompleteInspection = false,
                        Unknown = new Receipts.UnknownEffect { Reason = "No tracked construction effect is available for this attempt." } };
                return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = progress });
            }, cancellationToken).ConfigureAwait(false);
        }

        private static bool ValidAttempt(Common.AttemptKey value) => value != null && value.HasControllerSessionId
            && ProtoBoundary.IsIdentifier(value.ControllerSessionId) && value.HasActionId && ProtoBoundary.IsIdentifier(value.ActionId)
            && value.HasAttemptId && value.AttemptId > 0;
        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply
        { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
