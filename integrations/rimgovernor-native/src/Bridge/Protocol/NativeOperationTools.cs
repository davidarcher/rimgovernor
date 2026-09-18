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
        internal readonly Dictionary<Common.AttemptKey, NativeConstructionRecord> Construction = new Dictionary<Common.AttemptKey, NativeConstructionRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeDraftRecord> Drafts = new Dictionary<Common.AttemptKey, NativeDraftRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeMovementRecord> Movements = new Dictionary<Common.AttemptKey, NativeMovementRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeCombatRecord> Combat = new Dictionary<Common.AttemptKey, NativeCombatRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeProductionRecord> Bills = new Dictionary<Common.AttemptKey, NativeProductionRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeZoneRecord> Zones = new Dictionary<Common.AttemptKey, NativeZoneRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeZoneEditRecord> ZoneEdits = new Dictionary<Common.AttemptKey, NativeZoneEditRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeStockpilePatchRecord> StockpilePatches = new Dictionary<Common.AttemptKey, NativeStockpilePatchRecord>();
        internal readonly Dictionary<Common.AttemptKey, INativeAcquisitionRecord> Acquisition = new Dictionary<Common.AttemptKey, INativeAcquisitionRecord>();
        internal readonly Dictionary<Common.AttemptKey, Operations.PatchPawn> WorkSettings = new Dictionary<Common.AttemptKey, Operations.PatchPawn>();
        // PatchBuilding admissions of any implemented field (target_temperature via
        // NativeBuildingTemperature, medical via NativeBedMedical, plant_def via
        // NativeGrowerCrop), observed by field.
        internal readonly Dictionary<Common.AttemptKey, Operations.PatchBuilding> BuildingPatches = new Dictionary<Common.AttemptKey, Operations.PatchBuilding>();
        internal readonly Dictionary<Common.AttemptKey, Receipts.DesignationEffect> AllowedSupplies = new Dictionary<Common.AttemptKey, Receipts.DesignationEffect>();
        internal readonly Dictionary<Common.AttemptKey, NativeHaulRecord> Hauls = new Dictionary<Common.AttemptKey, NativeHaulRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeCustodyRecord> Custody = new Dictionary<Common.AttemptKey, NativeCustodyRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeRecoveryServiceRecord> RecoveryServices = new Dictionary<Common.AttemptKey, NativeRecoveryServiceRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeMoodReliefRecord> MoodRelief = new Dictionary<Common.AttemptKey, NativeMoodReliefRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeHusbandryRecord> Husbandry = new Dictionary<Common.AttemptKey, NativeHusbandryRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativePrisonerInteractionRecord> PrisonerInteractions = new Dictionary<Common.AttemptKey, NativePrisonerInteractionRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeWasteRecord> Waste = new Dictionary<Common.AttemptKey, NativeWasteRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeEquipRecord> Equips = new Dictionary<Common.AttemptKey, NativeEquipRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeCleanRecord> Cleans = new Dictionary<Common.AttemptKey, NativeCleanRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeRepairRecord> Repairs = new Dictionary<Common.AttemptKey, NativeRepairRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeTradeRecord> Trade = new Dictionary<Common.AttemptKey, NativeTradeRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeCaravanRecord> Caravans = new Dictionary<Common.AttemptKey, NativeCaravanRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeQuestRecord> Quests = new Dictionary<Common.AttemptKey, NativeQuestRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeSettlementGiftRecord> SettlementGifts = new Dictionary<Common.AttemptKey, NativeSettlementGiftRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeQuestFulfillRecord> QuestFulfills = new Dictionary<Common.AttemptKey, NativeQuestFulfillRecord>();
        internal readonly Dictionary<Common.AttemptKey, Operations.SetProductionPolicy> ProductionPolicies = new Dictionary<Common.AttemptKey, Operations.SetProductionPolicy>();
        internal readonly Dictionary<Common.AttemptKey, NativeSurgeryRecord> Surgeries = new Dictionary<Common.AttemptKey, NativeSurgeryRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeCaravanTravelRecord> CaravanTravels = new Dictionary<Common.AttemptKey, NativeCaravanTravelRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeNamingRecord> Naming = new Dictionary<Common.AttemptKey, NativeNamingRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeDialogRecord> Dialogs = new Dictionary<Common.AttemptKey, NativeDialogRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeResearchSelectRecord> ResearchSelections = new Dictionary<Common.AttemptKey, NativeResearchSelectRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeBedAssignRecord> BedAssignments = new Dictionary<Common.AttemptKey, NativeBedAssignRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeExcavationRecord> Excavation = new Dictionary<Common.AttemptKey, NativeExcavationRecord>();
        internal readonly Dictionary<Common.AttemptKey, NativeWallRemovalRecord> WallRemovals = new Dictionary<Common.AttemptKey, NativeWallRemovalRecord>();
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
        public NativeOperationTools() { NativeProductionTracking.Install(); NativeAcquisitionTracking.Install(); NativeConstructionTracking.Install(); NativePawnControlState.Initialize(); NativeCombatCausality.Initialize(); NativeRangedCausality.Initialize(); }

        [Tool("rimgovernor/operations_execute", Title = "Execute guarded native operation", Description = "Admit typed PlaceBuilding, exact supply Allow, work-only PatchPawn, temporary SetDrafted, MovePawn or melee, direct-bullet or supported injury-only explosive AttackTarget under current native authority. Movement and combat require an existing owned draft. Exact retries return their original receipt.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ExecuteReply.", Always = true)]
        public async Task<object> Execute(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations ExecuteRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_execute", request, Operations.ExecuteRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.ExecuteReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => ProtoBoundary.Encode(ExecuteNative(parsed)), cancellationToken).ConfigureAwait(false);
        }

        internal static Operations.ExecuteReply ExecuteNative(Operations.ExecuteRequest request)
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
            var prior = state.Ledger.Inspect("rimgovernor.operations.v1.Operations/Execute", request);
            if (prior.Kind != NativeAttemptLedger.DecisionKind.New) return prior.DecidedReply;
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AddBill) return NativeProductionBills.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.CreateZone) return NativeZoneCreation.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AcquireResource)
                return NativePlantAcquisition.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.PatchPawn)
                return NativeWorkSettings.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.PatchBuilding)
                return request.Operation.PatchBuilding.HasMedical
                    ? NativeBedMedical.Execute(state, request, context)
                    : request.Operation.PatchBuilding.HasPlantDef
                        ? NativeGrowerCrop.Execute(state, request, context)
                        : NativeBuildingTemperature.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.DesignateThing)
                return NativeSupplyAllow.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.SetDrafted)
                return NativeDraftOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.MovePawn)
                return NativeMovementOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AttackTarget)
                return NativeCombatOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.PawnTargetOrder)
            {
                switch (request.Operation.PawnTargetOrder.Kind)
                {
                    case Operations.PawnOrderKind.Equip: return NativeEquipOperations.Execute(state, request, context);
                    case Operations.PawnOrderKind.Haul: return NativeHaulOperations.Execute(state, request, context);
                    case Operations.PawnOrderKind.Capture:
                    case Operations.PawnOrderKind.Rescue: return NativeCustodyOperations.Execute(state, request, context);
                    case Operations.PawnOrderKind.Clean: return NativeCleanOperations.Execute(state, request, context);
                    case Operations.PawnOrderKind.Repair: return NativeRepairOperations.Execute(state, request, context);
                    default: return Refuse(Common.FailureCode.Unsupported, "This native adapter implements Equip, Haul, Capture, Rescue, Clean and Repair pawn-target orders.");
                }
            }
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.RecoverService)
                return NativeRecoveryOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.RelieveNeed)
                return NativeMoodReliefOperations.Execute(state, request, context);
            if (NativeHusbandryOperations.TryKind(request.Operation, out _))
                return NativeHusbandryOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.SetPrisonerInteraction)
                return NativePrisonerInteractionOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.ManageWaste)
                return NativeWasteOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.OpenTrade
                || request.Operation.CommandCase == Operations.Operation.CommandOneofCase.SetTradeLines
                || request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AcceptTrade
                || request.Operation.CommandCase == Operations.Operation.CommandOneofCase.EndTrade)
                return NativeTradeOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.FormCaravan)
                return NativeCaravanOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AcceptQuest)
                return NativeQuestOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.GiftCaravanSilver)
                return NativeSettlementGiftOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.FulfillQuest)
                return NativeQuestFulfillOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.SetProductionPolicy)
                return NativeProductionPolicyOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.QueueSurgery)
                return NativeSurgeryOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.TravelCaravan)
                return NativeCaravanTravel.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.ConfirmColonyNames)
                return NativeColonyNamingOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AnswerDialog)
                return NativeChoiceDialogOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.SelectResearch)
                return NativeResearchSelectOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.AssignBed)
                return NativeBedAssignOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.ExcavateCell)
                return NativeExcavationOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.RemoveWall)
                return NativeWallRemovalOperations.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.ReleaseWallRemovals)
                return NativeWallRemovalOperations.ExecuteRelease(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.DeleteZone)
                return NativeZoneDeletion.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.EditZoneCells)
                return NativeZoneCellEdit.Execute(state, request, context);
            if (request.Operation.CommandCase == Operations.Operation.CommandOneofCase.PatchStockpile)
                return NativeStockpilePatch.Execute(state, request, context);
            if (request.Operation.CommandCase != Operations.Operation.CommandOneofCase.PlaceBuilding)
                return Refuse(Common.FailureCode.Unsupported, "This native adapter implements PlaceBuilding, temporary owned SetDrafted, exact owned MovePawn and melee, direct-bullet or supported injury-only explosive AttackTarget.");
            if (!NativeConstructionTracking.Ready)
                return Refuse(Common.FailureCode.Unavailable, "Construction transition tracking is unavailable.");
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                return Refuse(Common.FailureCode.AuthorityRequired, "Native authority has not been acquired.");
            var guard = authority.Check(precondition.ExpectedGeneration);
            context.NativeGeneration = guard.Snapshot.Generation;
            if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
            NativeConstructionPlan? plan;
            try
            {
                if (!NativeConstructionPlan.Prepare(ProtoBoundary.LoadedMap(context), request.Operation.PlaceBuilding.Placement, context, out plan, out _, out var invalid))
                    return new Operations.ExecuteReply { Failure = invalid };
            }
            catch (Exception error) { return Refuse(Common.FailureCode.NativeFailure, "Construction validation failed: " + error.GetType().Name); }
            guard = authority.Check(precondition.ExpectedGeneration);
            context.NativeGeneration = guard.Snapshot.Generation;
            if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
            if (!NativeConstructionTracking.Ready)
                return Refuse(Common.FailureCode.Unavailable, "Construction transition tracking is unavailable.");
            var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
            if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
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
                    return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, admission.AdmittedHandle,
                        precondition.Attempt, context, new Receipts.EffectEvidence { Construction = record.Effect }) };
                }
            }
            catch (Exception error)
            {
                var lastObserved = state.Construction.TryGetValue(precondition.Attempt, out var record)
                    ? new Receipts.EffectEvidence { Construction = record.Effect.Clone() }
                    : observed.CancelledFrameIds.Count > 0 || observed.WipedThingIds.Count > 0
                        ? new Receipts.EffectEvidence { Construction = observed } : null;
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, admission.AdmittedHandle,
                    precondition.Attempt, context, lastObserved, "Admitted construction requires observation: " + error.GetType().Name) };
            }
        }

        [Tool("rimgovernor/operations_preview", Title = "Preview typed operation", Description = "Read ordinary construction, exact supply Allow, work priorities, drafting, movement or melee, direct-bullet or supported injury-only explosive attack eligibility without acquiring authority or applying effects.")]
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
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AddBill) return ProtoBoundary.Encode(NativeProductionBills.Preview(parsed.Operation.AddBill, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.CreateZone) return ProtoBoundary.Encode(NativeZoneCreation.Preview(parsed.Operation.CreateZone, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AcquireResource)
                    return ProtoBoundary.Encode(NativePlantAcquisition.Preview(parsed.Operation.AcquireResource, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.PatchPawn)
                    return ProtoBoundary.Encode(NativeWorkSettings.Preview(parsed.Operation.PatchPawn, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.PatchBuilding)
                    return ProtoBoundary.Encode(parsed.Operation.PatchBuilding.HasMedical
                        ? NativeBedMedical.Preview(parsed.Operation.PatchBuilding, context)
                        : parsed.Operation.PatchBuilding.HasPlantDef
                            ? NativeGrowerCrop.Preview(parsed.Operation.PatchBuilding, context)
                            : NativeBuildingTemperature.Preview(parsed.Operation.PatchBuilding, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.DesignateThing)
                    return ProtoBoundary.Encode(NativeSupplyAllow.Preview(parsed.Operation.DesignateThing, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.MovePawn)
                    return ProtoBoundary.Encode(NativeMovementOperations.Preview(parsed.Operation.MovePawn, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AttackTarget)
                    return ProtoBoundary.Encode(NativeCombatOperations.Preview(parsed.Operation.AttackTarget, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.PawnTargetOrder)
                {
                    switch (parsed.Operation.PawnTargetOrder.Kind)
                    {
                        case Operations.PawnOrderKind.Equip: return ProtoBoundary.Encode(NativeEquipOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                        case Operations.PawnOrderKind.Haul: return ProtoBoundary.Encode(NativeHaulOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                        case Operations.PawnOrderKind.Capture:
                        case Operations.PawnOrderKind.Rescue: return ProtoBoundary.Encode(NativeCustodyOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                        case Operations.PawnOrderKind.Clean: return ProtoBoundary.Encode(NativeCleanOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                        case Operations.PawnOrderKind.Repair: return ProtoBoundary.Encode(NativeRepairOperations.Preview(parsed.Operation.PawnTargetOrder, context));
                        default: return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Preview implements Equip, Haul, Capture, Rescue, Clean and Repair pawn-target orders.") });
                    }
                }
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.RecoverService)
                    return ProtoBoundary.Encode(NativeRecoveryOperations.Preview(parsed.Operation.RecoverService, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.RelieveNeed)
                    return ProtoBoundary.Encode(NativeMoodReliefOperations.Preview(parsed.Operation.RelieveNeed, context));
                if (parsed.Operation != null && NativeHusbandryOperations.TryKind(parsed.Operation, out _))
                    return ProtoBoundary.Encode(NativeHusbandryOperations.Preview(parsed.Operation, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.SetPrisonerInteraction)
                    return ProtoBoundary.Encode(NativePrisonerInteractionOperations.Preview(parsed.Operation.SetPrisonerInteraction, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.ManageWaste)
                    return ProtoBoundary.Encode(NativeWasteOperations.Preview(parsed.Operation.ManageWaste, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.OpenTrade
                    || parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.SetTradeLines
                    || parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AcceptTrade
                    || parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.EndTrade)
                    return ProtoBoundary.Encode(NativeTradeOperations.Preview(parsed.Operation, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.FormCaravan)
                    return ProtoBoundary.Encode(NativeCaravanOperations.Preview(parsed.Operation.FormCaravan, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AcceptQuest)
                    return ProtoBoundary.Encode(NativeQuestOperations.Preview(parsed.Operation.AcceptQuest, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.GiftCaravanSilver)
                    return ProtoBoundary.Encode(NativeSettlementGiftOperations.Preview(parsed.Operation.GiftCaravanSilver, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.FulfillQuest)
                    return ProtoBoundary.Encode(NativeQuestFulfillOperations.Preview(parsed.Operation.FulfillQuest, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.SetProductionPolicy)
                    return ProtoBoundary.Encode(NativeProductionPolicyOperations.Preview(parsed.Operation.SetProductionPolicy, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.QueueSurgery)
                    return ProtoBoundary.Encode(NativeSurgeryOperations.Preview(parsed.Operation.QueueSurgery, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.TravelCaravan)
                    return ProtoBoundary.Encode(NativeCaravanTravel.Preview(parsed.Operation.TravelCaravan, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.ConfirmColonyNames)
                    return ProtoBoundary.Encode(NativeColonyNamingOperations.Preview(parsed.Operation.ConfirmColonyNames, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AnswerDialog)
                    return ProtoBoundary.Encode(NativeChoiceDialogOperations.Preview(parsed.Operation.AnswerDialog, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.SelectResearch)
                    return ProtoBoundary.Encode(NativeResearchSelectOperations.Preview(parsed.Operation.SelectResearch, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.AssignBed)
                    return ProtoBoundary.Encode(NativeBedAssignOperations.Preview(parsed.Operation.AssignBed, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.ExcavateCell)
                    return ProtoBoundary.Encode(NativeExcavationOperations.Preview(parsed.Operation.ExcavateCell, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.RemoveWall)
                    return ProtoBoundary.Encode(NativeWallRemovalOperations.Preview(parsed.Operation.RemoveWall, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.ReleaseWallRemovals)
                    return ProtoBoundary.Encode(NativeWallRemovalOperations.PreviewRelease(parsed.Operation.ReleaseWallRemovals, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.DeleteZone)
                    return ProtoBoundary.Encode(NativeZoneDeletion.Preview(parsed.Operation.DeleteZone, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.EditZoneCells)
                    return ProtoBoundary.Encode(NativeZoneCellEdit.Preview(parsed.Operation.EditZoneCells, context));
                if (parsed.Operation?.CommandCase == Operations.Operation.CommandOneofCase.PatchStockpile)
                    return ProtoBoundary.Encode(NativeStockpilePatch.Preview(parsed.Operation.PatchStockpile, context));
                if (parsed.Operation == null || parsed.Operation.CommandCase != Operations.Operation.CommandOneofCase.PlaceBuilding)
                    return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Preview implements PlaceBuilding, temporary SetDrafted, exact owned MovePawn and melee, direct-bullet or supported injury-only explosive AttackTarget.") });
                var accepted = NativeConstructionPlan.Prepare(ProtoBoundary.LoadedMap(context), parsed.Operation.PlaceBuilding.Placement, context, out _, out var preview, out var rejected);
                if (preview == null) return ProtoBoundary.Encode(new Operations.PreviewReply { Failure = rejected });
                return ProtoBoundary.Encode(NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation
                {
                    Context = context, Accepted = accepted, Reason = rejected?.Detail ?? "", Placement = preview
                } }));
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

        [Tool("rimgovernor/receipts_observe_progress", Title = "Observe admitted operation", Description = "Read causally tracked construction, supply Allow, work priorities, draft, movement or melee, direct-bullet or supported injury-only explosive outcomes; absence of an attempt never proves completion.")]
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
                NativeConstructionRecord record;
                if (NativeOperationState.TryGet(context.Identity, out var state))
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
                    Operations.PatchBuilding buildingPatch;
                    if (state.BuildingPatches.TryGetValue(parsed.Attempt, out buildingPatch))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = buildingPatch.HasMedical
                            ? NativeBedMedical.Observe(parsed.Attempt, context, buildingPatch)
                            : buildingPatch.HasPlantDef
                                ? NativeGrowerCrop.Observe(parsed.Attempt, context, buildingPatch)
                                : NativeBuildingTemperature.Observe(parsed.Attempt, context, buildingPatch) }));
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
                    NativeCustodyRecord custody;
                    if (state.Custody.TryGetValue(parsed.Attempt, out custody))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = custody.Observe(parsed.Attempt, context) }));
                    NativeRecoveryServiceRecord recovery;
                    if (state.RecoveryServices.TryGetValue(parsed.Attempt, out recovery))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = recovery.Observe(parsed.Attempt, context) }));
                    NativeMoodReliefRecord relief;
                    if (state.MoodRelief.TryGetValue(parsed.Attempt, out relief))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = relief.Observe(parsed.Attempt, context) }));
                    NativeHusbandryRecord husbandry;
                    if (state.Husbandry.TryGetValue(parsed.Attempt, out husbandry))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeHusbandryOperations.Observe(parsed.Attempt, context, husbandry) }));
                    NativePrisonerInteractionRecord prisonerInteraction;
                    if (state.PrisonerInteractions.TryGetValue(parsed.Attempt, out prisonerInteraction))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativePrisonerInteractionOperations.Observe(parsed.Attempt, context, prisonerInteraction) }));
                    NativeWasteRecord waste;
                    if (state.Waste.TryGetValue(parsed.Attempt, out waste))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = waste.Observe(parsed.Attempt, context) }));
                    NativeEquipRecord equip;
                    if (state.Equips.TryGetValue(parsed.Attempt, out equip))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = equip.Observe(parsed.Attempt, context) }));
                    NativeCleanRecord clean;
                    if (state.Cleans.TryGetValue(parsed.Attempt, out clean))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = clean.Observe(parsed.Attempt, context) }));
                    NativeRepairRecord repair;
                    if (state.Repairs.TryGetValue(parsed.Attempt, out repair))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = repair.Observe(parsed.Attempt, context) }));
                    NativeTradeRecord trade;
                    if (state.Trade.TryGetValue(parsed.Attempt, out trade))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = trade.Observe(parsed.Attempt, context) }));
                    NativeCaravanRecord caravan;
                    if (state.Caravans.TryGetValue(parsed.Attempt, out caravan))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeCaravanOperations.Observe(parsed.Attempt, context, caravan) }));
                    NativeQuestRecord quest;
                    if (state.Quests.TryGetValue(parsed.Attempt, out quest))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeQuestOperations.Observe(parsed.Attempt, context, quest) }));
                    NativeSettlementGiftRecord settlementGift;
                    if (state.SettlementGifts.TryGetValue(parsed.Attempt, out settlementGift))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeSettlementGiftOperations.Observe(parsed.Attempt, context, settlementGift) }));
                    NativeQuestFulfillRecord questFulfill;
                    if (state.QuestFulfills.TryGetValue(parsed.Attempt, out questFulfill))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeQuestFulfillOperations.Observe(parsed.Attempt, context, questFulfill) }));
                    Operations.SetProductionPolicy productionPolicy;
                    if (state.ProductionPolicies.TryGetValue(parsed.Attempt, out productionPolicy))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeProductionPolicyOperations.Observe(parsed.Attempt, context, productionPolicy) }));
                    NativeSurgeryRecord surgery;
                    if (state.Surgeries.TryGetValue(parsed.Attempt, out surgery))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = surgery.Observe(parsed.Attempt, context) }));
                    NativeCaravanTravelRecord caravanTravel;
                    if (state.CaravanTravels.TryGetValue(parsed.Attempt, out caravanTravel))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeCaravanTravel.Observe(parsed.Attempt, context, caravanTravel) }));
                    NativeNamingRecord naming;
                    if (state.Naming.TryGetValue(parsed.Attempt, out naming))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeColonyNamingOperations.Observe(parsed.Attempt, context, naming) }));
                    NativeDialogRecord dialog;
                    if (state.Dialogs.TryGetValue(parsed.Attempt, out dialog))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeChoiceDialogOperations.Observe(parsed.Attempt, context, dialog) }));
                    NativeResearchSelectRecord researchSelect;
                    if (state.ResearchSelections.TryGetValue(parsed.Attempt, out researchSelect))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = NativeResearchSelectOperations.Observe(parsed.Attempt, context, researchSelect) }));
                    NativeBedAssignRecord bedAssign;
                    if (state.BedAssignments.TryGetValue(parsed.Attempt, out bedAssign))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = bedAssign.Observe(parsed.Attempt, context) }));
                    NativeExcavationRecord excavation;
                    if (state.Excavation.TryGetValue(parsed.Attempt, out excavation))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = excavation.Observe(parsed.Attempt, context) }));
                    NativeWallRemovalRecord wallRemoval;
                    if (state.WallRemovals.TryGetValue(parsed.Attempt, out wallRemoval))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = wallRemoval.Observe(parsed.Attempt, context) }));
                    NativeZoneEditRecord zoneEdit;
                    if (state.ZoneEdits.TryGetValue(parsed.Attempt, out zoneEdit))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = zoneEdit.Observe(parsed.Attempt, context) }));
                    NativeStockpilePatchRecord stockpilePatch;
                    if (state.StockpilePatches.TryGetValue(parsed.Attempt, out stockpilePatch))
                        return ProtoBoundary.Encode(NativeOperationEnvelope.Progress(new Receipts.ProgressReply { Progress = stockpilePatch.Observe(parsed.Attempt, context) }));
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
            [ToolParameter(Description = "Official operations ReleaseOwnedDraftRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_release_owned_draft", request, Operations.ReleaseOwnedDraftRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.ReleaseOwnedDraftReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => ProtoBoundary.Encode(NativeDraftOperations.Release(parsed)), cancellationToken).ConfigureAwait(false);
        }

        private static bool ValidAttempt([NotNullWhen(true)] Common.AttemptKey? value) => value != null && value.HasControllerSessionId
            && ProtoBoundary.IsIdentifier(value.ControllerSessionId) && value.HasActionId && ProtoBoundary.IsIdentifier(value.ActionId)
            && value.HasAttemptId && value.AttemptId > 0;
        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply
        { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
