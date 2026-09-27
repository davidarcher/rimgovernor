#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
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
        internal readonly Dictionary<Common.AttemptKey, NativeDraftRecord> Drafts = new Dictionary<Common.AttemptKey, NativeDraftRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeCombatRecord> Combat = new Dictionary<Common.AttemptKey, NativeCombatRecord>();
        internal readonly Dictionary<Common.AttemptKey, INativeAcquisitionRecord> Acquisition = new Dictionary<Common.AttemptKey, INativeAcquisitionRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeCustodyRecord> Custody = new Dictionary<Common.AttemptKey, NativeCustodyRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeMoodReliefRecord> MoodRelief = new Dictionary<Common.AttemptKey, NativeMoodReliefRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeEquipRecord> Equips = new Dictionary<Common.AttemptKey, NativeEquipRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeGearRecord> Gear = new Dictionary<Common.AttemptKey, NativeGearRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeSubdueRecord> Subdues = new Dictionary<Common.AttemptKey, NativeSubdueRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeArrestRecord> Arrests = new Dictionary<Common.AttemptKey, NativeArrestRecord>();
        private NativeOperationState(Common.Identity identity)
        { colony = identity.ColonyId; load = identity.LoadToken; Ledger = new NativeAttemptLedger(identity); }
        internal static bool TryGet(Common.Identity identity, [NotNullWhen(true)] out NativeOperationState? state)
        {
            state = null;
            return Current.Game != null && States.TryGetValue(Current.Game, out state)
                && state.colony == identity.ColonyId && state.load == identity.LoadToken;
        }
        internal static NativeOperationState ForAdmission(Common.Identity identity)
        {
            if (TryGet(identity, out var state)) return state;
            States.Remove(Current.Game);
            var created = new NativeOperationState(identity); States.Add(Current.Game, created); return created;
        }
    }

    public sealed class NativeOperationTools
    {
        public NativeOperationTools() { NativeDrugPolicy.Install(); NativeAcquisitionTracking.Install(); NativePawnControlState.Initialize(); NativeCombatCausality.Initialize(); NativeRangedCausality.Initialize(); }

        [Tool("rimgovernor/operations_execute", Title = "Execute guarded native operation", Description = "Admit exact supply Allow, temporary SetDrafted, melee, direct-bullet or supported injury-only explosive AttackTarget, or a batched CombatOrders, under current native authority. Combat requires an existing owned draft. Exact retries return their original receipt.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ExecuteReply.", Always = true)]
        public async Task<object> Execute(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations ExecuteRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_execute", request, Operations.ExecuteRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.ExecuteReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => ProtoBoundary.Encode(ExecuteNative(parsed)), cancellationToken).ConfigureAwait(false);
        }

        // Jobs, blueprints and designations made inside the op carry its
        // intent to the activity overlay (#822).
        internal static Operations.ExecuteReply ExecuteNative(Operations.ExecuteRequest request)
        {
            // The next snapshot frame reflects the op, applied or refused (#858).
            try
            {
                using (OperationIntent.Scope(request.Operation?.HasIntent == true ? request.Operation.Intent : null))
                    return ExecuteNativeCore(request);
            }
            finally { SnapshotStream.NoteWrite(); }
        }

        private static Operations.ExecuteReply ExecuteNativeCore(Operations.ExecuteRequest request)
        {
            var precondition = request.Precondition;
            if (precondition == null || !precondition.HasExpectedGeneration || precondition.ExpectedGeneration == 0
                || !ValidAttempt(precondition.Attempt))
                return Refuse(Common.FailureCode.InvalidRequest, "A complete authority precondition and positive attempt are required.");
            if (!ProtoBoundary.ValidateIdentity(precondition.Identity, out var context, out var failure))
                return new Operations.ExecuteReply { Failure = failure };
            if (request.Operation == null || request.Operation.CommandCase == Operations.Operation.CommandOneofCase.None)
                return Refuse(Common.FailureCode.InvalidRequest, "An operation is required.");
            var state = NativeOperationState.ForAdmission(context.Identity);
            NativeDrugPolicy.Install();
            var prior = state.Ledger.Inspect("rimgovernor.operations.v1.Operations/Execute", request);
            if (prior.Kind != NativeAttemptLedger.DecisionKind.New) return prior.DecidedReply;
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AcquireResource)
                return NativePlantAcquisition.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.CancelAcquisition)
                return NativePlantAcquisition.Cancel(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.SetDrafted)
                return NativeDraftOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AttackTarget)
                return NativeCombatOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.CombatOrders)
                return NativeCombatOrders.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.PawnTargetOrder)
            {
                switch (request.Operation.PawnTargetOrder.Kind)
                {
                    case Operations.PawnOrderKind.Subdue: return NativeSubdueOperations.Execute(state, request, context);
                    case Operations.PawnOrderKind.Equip: return NativeEquipOperations.Execute(state, request, context);
                    case Operations.PawnOrderKind.Capture:
                    case Operations.PawnOrderKind.Rescue: return NativeCustodyOperations.Execute(state, request, context);
                    default: return Refuse(Common.FailureCode.Unsupported, "This native adapter implements Equip, Haul, Capture, Rescue, Clean, Repair, OpenCasket and Tend pawn-target orders.");
                }
            }
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.ImproveGear)
                return NativeGearOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.RelieveNeed)
                return NativeMoodReliefOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.Arrest)
                return NativeArrestOperations.Execute(state, request, context);
            return Refuse(Common.FailureCode.Unsupported, "This native adapter does not implement the " + request.Operation.CommandCase + " operation; buildings are placed through Actions/Apply.");
        }

        [Tool("rimgovernor/operations_preview", Title = "Preview typed operation", Description = "Read exact supply Allow, work priorities, drafting, movement or melee, direct-bullet or supported injury-only explosive attack eligibility without acquiring authority or applying effects.")]
        [ToolResponse("payload", "string", "Official ProtoJSON PreviewReply.", Always = true)]
        public async Task<object> Preview(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations PreviewRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_preview", request, Operations.PreviewRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                    return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = invalid });
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.SetDrafted)
                    return ProtoBoundary.Encode(NativeDraftOperations.Preview(parsed.Operation.SetDrafted, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.CreateZone) return ProtoBoundary.Encode(NativeZoneCreation.Preview(parsed.Operation.CreateZone, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AcquireResource)
                    return ProtoBoundary.Encode(NativePlantAcquisition.Preview(parsed.Operation.AcquireResource, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AttackTarget)
                    return ProtoBoundary.Encode(NativeCombatOperations.Preview(parsed.Operation.AttackTarget, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.PawnTargetOrder)
                {
                    switch (parsed.Operation.PawnTargetOrder.Kind)
                    {
                        case Operations.PawnOrderKind.Subdue: return ProtoBoundary.Encode(NativeSubdueOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                        case Operations.PawnOrderKind.Equip: return ProtoBoundary.Encode(NativeEquipOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                        case Operations.PawnOrderKind.Capture:
                        case Operations.PawnOrderKind.Rescue: return ProtoBoundary.Encode(NativeCustodyOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                        default: return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Preview implements Equip, Capture, Rescue, Clean, Repair, OpenCasket and Tend pawn-target orders.") });
                    }
                }
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.ImproveGear)
                    return ProtoBoundary.Encode(NativeGearOperations.Preview(parsed.Operation.ImproveGear, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.RelieveNeed)
                    return ProtoBoundary.Encode(NativeMoodReliefOperations.Preview(parsed.Operation.RelieveNeed, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.Arrest)
                    return ProtoBoundary.Encode(NativeArrestOperations.Preview(parsed.Operation.Arrest, context));
                return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Preview does not implement this operation; building placement previews through rimgovernor/placement_preview.") });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/receipts_lookup", Title = "Look up admitted attempt", Description = "Read original admitted receipt without requiring current authority.")]
        [ToolResponse("payload", "string", "Official ProtoJSON LookupReply.", Always = true)]
        public async Task<object> Lookup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official receipts LookupRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/receipts_lookup", request, Receipts.LookupRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Receipts.LookupReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ValidAttempt(parsed.Attempt)) return ProtoBoundary.Encode(new Receipts.LookupReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A complete attempt is required.") });
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                    return ProtoBoundary.Encode(new Receipts.LookupReply { Failure = invalid });
                return ProtoBoundary.Encode(NativeOperationState.TryGet(context.Identity, out var state)
                    ? state.Ledger.Lookup(parsed.Attempt, context)
                    : new Receipts.LookupReply { Unknown = new Receipts.UnknownAttempt { Context = context } });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/receipts_observe_progress", Title = "Observe admitted operation", Description = "Read causally tracked supply Allow, work priorities, draft, movement or melee, direct-bullet or supported injury-only explosive outcomes; absence of an attempt never proves completion.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ProgressReply.", Always = true)]
        public async Task<object> ObserveProgress(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official receipts ProgressRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/receipts_observe_progress", request, Receipts.ProgressRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Receipts.ProgressReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ValidAttempt(parsed.Attempt)) return ProtoBoundary.Encode(new Receipts.ProgressReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A complete attempt is required.") });
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                    return ProtoBoundary.Encode(new Receipts.ProgressReply { Failure = invalid });
                if (NativeOperationState.TryGet(context.Identity, out var state))
                {
                    var lookup = state.Ledger.Lookup(parsed.Attempt, context);
                    if (lookup.Failure != null) return ProtoBoundary.Encode(new Receipts.ProgressReply { Failure = lookup.Failure });
                    INativeAcquisitionRecord acquisition;
                    if (state.Acquisition.TryGetValue(parsed.Attempt, out acquisition))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = acquisition.Observe(parsed.Attempt, context) });
                    NativeCombatRecord combat;
                    if (state.Combat.TryGetValue(parsed.Attempt, out combat))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = combat.Observe(parsed.Attempt, context) });
                    NativeDraftRecord draft;
                    if (state.Drafts.TryGetValue(parsed.Attempt, out draft))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = draft.Observe(parsed.Attempt, context) });
                    NativeCustodyRecord custody;
                    if (state.Custody.TryGetValue(parsed.Attempt, out custody))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = custody.Observe(parsed.Attempt, context) });
                    NativeMoodReliefRecord relief;
                    if (state.MoodRelief.TryGetValue(parsed.Attempt, out relief))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = relief.Observe(parsed.Attempt, context) });
                    NativeEquipRecord equip;
                    if (state.Equips.TryGetValue(parsed.Attempt, out equip))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = equip.Observe(parsed.Attempt, context) });
                    NativeGearRecord gear;
                    if (state.Gear.TryGetValue(parsed.Attempt, out gear))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = gear.Observe(parsed.Attempt, context) });
                    if (state.Subdues.TryGetValue(parsed.Attempt, out var subdue))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = subdue.Observe(parsed.Attempt, context) });
                    if (state.Arrests.TryGetValue(parsed.Attempt, out var arrest))
                        return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = arrest.Observe(parsed.Attempt, context) });
                }
                var progress = new Receipts.Progress { Attempt = parsed.Attempt.Clone(), Context = context, CompleteInspection = false,
                    Unknown = new Receipts.UnknownEffect { Reason = "No tracked effect is available for this attempt." } };
                return ProtoBoundary.Encode(new Receipts.ProgressReply { Progress = progress });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/operations_release_owned_draft", Title = "Release exact owned draft", Description = "Release an unchanged native draft claim under its original owner/direction, including after Manual or lease expiry. Independent of ordinary attempt capacity; never adopts or clears replacement player orders.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ReleaseOwnedDraftReply.", Always = true)]
        public async Task<object> ReleaseOwnedDraft(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations ReleaseOwnedDraftRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_release_owned_draft", request, Operations.ReleaseOwnedDraftRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.ReleaseOwnedDraftReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => { try { return ProtoBoundary.Encode(NativeDraftOperations.Release(parsed)); } finally { SnapshotStream.NoteWrite(); } }, cancellationToken).ConfigureAwait(false);
        }

        private static bool ValidAttempt([NotNullWhen(true)] Common.AttemptKey? value) => value != null && value.HasControllerSessionId
            && ProtoBoundary.IsIdentifier(value.ControllerSessionId) && value.HasActionId && ProtoBoundary.IsIdentifier(value.ActionId)
            && value.HasAttemptId && value.AttemptId > 0;
        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply
        { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
