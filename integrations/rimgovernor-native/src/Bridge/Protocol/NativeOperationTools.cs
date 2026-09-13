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
        internal readonly Dictionary<Common.AttemptKey, NativeDraftRecord> Drafts = new Dictionary<Common.AttemptKey, NativeDraftRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeMovementRecord> Movements = new Dictionary<Common.AttemptKey, NativeMovementRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeCombatRecord> Combat = new Dictionary<Common.AttemptKey, NativeCombatRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeProductionRecord> Bills = new Dictionary<Common.AttemptKey, NativeProductionRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeZoneRecord> Zones = new Dictionary<Common.AttemptKey, NativeZoneRecord>();
        internal readonly Dictionary<Common.AttemptKey, INativeAcquisitionRecord> Acquisition = new Dictionary<Common.AttemptKey, INativeAcquisitionRecord>();
        internal readonly Dictionary<Common.AttemptKey, Operations.PatchPawn> WorkSettings = new Dictionary<Common.AttemptKey, Operations.PatchPawn>();
        internal readonly Dictionary<Common.AttemptKey, Receipts.DesignationEffect> AllowedSupplies = new Dictionary<Common.AttemptKey, Receipts.DesignationEffect>();
        internal readonly Dictionary<Common.AttemptKey, NativeHaulRecord> Hauls = new Dictionary<Common.AttemptKey, NativeHaulRecord>();
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
        public NativeOperationTools() { NativeProductionTracking.Install(); NativeAcquisitionTracking.Install(); NativeConstructionTracking.Install(); NativePawnControlState.Initialize(); NativeCombatCausality.Initialize(); NativeRangedCausality.Initialize(); }

        [Tool("rimgovernor/operations_execute", Title = "Execute guarded native operation", Description = "Admit typed PlaceBuilding, exact supply Allow, work-only PatchPawn, temporary SetDrafted, MovePawn or melee, direct-bullet or supported injury-only explosive AttackTarget under current native authority. Movement and combat require an existing owned draft. Exact retries return their original receipt.")]
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
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AddBill) return NativeProductionBills.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.CreateZone) return NativeZoneCreation.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AcquireResource)
                return NativePlantAcquisition.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.PatchPawn)
                return NativeWorkSettings.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.DesignateThing)
                return NativeSupplyAllow.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.SetDrafted)
                return NativeDraftOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.MovePawn)
                return NativeMovementOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AttackTarget)
                return NativeCombatOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.PawnTargetOrder)
                return NativeHaulOperations.Execute(state, request, context);
            if (request.Operation.CommandCase != Operations.Operation.CommandOneofCase.PlaceBuilding)
                return Refuse(Common.FailureCode.Unsupported, "This native adapter implements PlaceBuilding, temporary owned SetDrafted, exact owned MovePawn and melee, direct-bullet or supported injury-only explosive AttackTarget.");
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
            if (!NativeConstructionTracking.Ready)
                return Refuse(Common.FailureCode.Unavailable, "Construction transition tracking is unavailable.");
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
                    return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, admission.Handle,
                        precondition.Attempt, context, owner, new Receipts.EffectEvidence { Construction = record.Effect }) };
                }
            }
            catch (Exception error)
            {
                NativeConstructionRecord record;
                var lastObserved = state.Construction.TryGetValue(precondition.Attempt, out record)
                    ? new Receipts.EffectEvidence { Construction = record.Effect.Clone() }
                    : observed.CancelledFrameIds.Count > 0 || observed.WipedThingIds.Count > 0
                        ? new Receipts.EffectEvidence { Construction = observed } : null;
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, admission.Handle,
                    precondition.Attempt, context, owner, lastObserved, "Admitted construction requires observation: " + error.GetType().Name) };
            }
        }

        [Tool("rimgovernor/operations_preview", Title = "Preview typed operation", Description = "Read ordinary construction, exact supply Allow, work priorities, drafting, movement or melee, direct-bullet or supported injury-only explosive attack eligibility without acquiring authority or applying effects.")]
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
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.SetDrafted)
                    return ProtoBoundary.Encode(NativeDraftOperations.Preview(parsed.Operation.SetDrafted, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AddBill) return ProtoBoundary.Encode(NativeProductionBills.Preview(parsed.Operation.AddBill, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.CreateZone) return ProtoBoundary.Encode(NativeZoneCreation.Preview(parsed.Operation.CreateZone, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AcquireResource)
                    return ProtoBoundary.Encode(NativePlantAcquisition.Preview(parsed.Operation.AcquireResource, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.PatchPawn)
                    return ProtoBoundary.Encode(NativeWorkSettings.Preview(parsed.Operation.PatchPawn, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.DesignateThing)
                    return ProtoBoundary.Encode(NativeSupplyAllow.Preview(parsed.Operation.DesignateThing, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.MovePawn)
                    return ProtoBoundary.Encode(NativeMovementOperations.Preview(parsed.Operation.MovePawn, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AttackTarget)
                    return ProtoBoundary.Encode(NativeCombatOperations.Preview(parsed.Operation.AttackTarget, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.PawnTargetOrder)
                    return ProtoBoundary.Encode(NativeHaulOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                if (parsed.Operation == null || parsed.Operation.CommandCase != Operations.Operation.CommandOneofCase.PlaceBuilding)
                    return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Preview implements PlaceBuilding, temporary SetDrafted, exact owned MovePawn and melee, direct-bullet or supported injury-only explosive AttackTarget.") });
                NativeConstructionPlan plan; RimGovernor.Protocol.Placement.PlacementEvaluated preview;
                var accepted = NativeConstructionPlan.Prepare(Find.CurrentMap, parsed.Operation.PlaceBuilding.Placement, context, out plan, out preview, out invalid);
                if (preview == null) return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = invalid });
                return ProtoBoundary.Encode(NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation
                {
                    Context = context, Accepted = accepted, Reason = accepted ? "" : invalid.Detail, Placement = preview
                } }));
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

        [Tool("rimgovernor/receipts_observe_progress", Title = "Observe admitted operation", Description = "Read causally tracked construction, supply Allow, work priorities, draft, movement or melee, direct-bullet or supported injury-only explosive outcomes; absence of an attempt never proves completion.")]
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
                    Receipts.DesignationEffect allowed;
                    NativeProductionRecord bill;
                    if (state.Bills.TryGetValue(parsed.Attempt, out bill)) return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = bill.Observe(parsed.Attempt, context) }));
                    NativeZoneRecord zone;
                    if (state.Zones.TryGetValue(parsed.Attempt, out zone)) return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = zone.Observe(parsed.Attempt, context) }));
                    INativeAcquisitionRecord acquisition;
                    if (state.Acquisition.TryGetValue(parsed.Attempt, out acquisition))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = acquisition.Observe(parsed.Attempt, context) }));
                    Operations.PatchPawn work;
                    if (state.WorkSettings.TryGetValue(parsed.Attempt, out work))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeWorkSettings.Observe(parsed.Attempt, context, work) }));
                    if (state.AllowedSupplies.TryGetValue(parsed.Attempt, out allowed))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeSupplyAllow.Observe(parsed.Attempt, context, allowed) }));
                    NativeCombatRecord combat;
                    if (state.Combat.TryGetValue(parsed.Attempt, out combat))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = combat.Observe(parsed.Attempt, context) }));
                    NativeMovementRecord movement;
                    if (state.Movements.TryGetValue(parsed.Attempt, out movement))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = movement.Observe(parsed.Attempt, context) }));
                    NativeDraftRecord draft;
                    if (state.Drafts.TryGetValue(parsed.Attempt, out draft))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = draft.Observe(parsed.Attempt, context) }));
                    NativeHaulRecord haul;
                    if (state.Hauls.TryGetValue(parsed.Attempt, out haul))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = haul.Observe(parsed.Attempt, context) }));
                }
                var progress = NativeOperationState.TryGet(context.Identity, out state) && state.Construction.TryGetValue(parsed.Attempt, out record)
                    ? record.Observe(parsed.Attempt, context)
                    : new Receipts.Progress { Attempt = parsed.Attempt.Clone(), Context = context, CompleteInspection = false,
                        Unknown = new Receipts.UnknownEffect { Reason = "No tracked construction effect is available for this attempt." } };
                return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = progress }));
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/operations_release_owned_draft", Title = "Release exact owned draft", Description = "Release an unchanged native draft claim under its original owner/direction, including after Manual or lease expiry. Independent of ordinary attempt capacity; never adopts or clears replacement player orders.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ReleaseOwnedDraftReply.", Always = true)]
        public async Task<object> ReleaseOwnedDraft(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations ReleaseOwnedDraftRequest ProtoJSON string.")] object request = null)
        {
            Operations.ReleaseOwnedDraftRequest parsed; Common.Failure failure;
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_release_owned_draft", request, Operations.ReleaseOwnedDraftRequest.Parser, out parsed, out failure))
                return ProtoBoundary.Encode(new Operations.ReleaseOwnedDraftReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => ProtoBoundary.Encode(NativeDraftOperations.Release(parsed)), cancellationToken).ConfigureAwait(false);
        }

        private static bool ValidAttempt(Common.AttemptKey value) => value != null && value.HasControllerSessionId
            && ProtoBoundary.IsIdentifier(value.ControllerSessionId) && value.HasActionId && ProtoBoundary.IsIdentifier(value.ActionId)
            && value.HasAttemptId && value.AttemptId > 0;
        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply
        { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
