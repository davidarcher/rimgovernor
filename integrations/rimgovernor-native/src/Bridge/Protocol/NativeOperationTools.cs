#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Authority = RimGovernor.Contracts.Wire.Authority;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeOperationState
    {
        private static readonly ConditionalWeakTable<Game, NativeOperationState> States = new ConditionalWeakTable<Game, NativeOperationState>();
        private readonly string colony;
        private readonly string load;
        internal readonly NativeAttemptLedger Ledger;
        private NativeOperationState(Common.Identity identity)
        { colony = identity.ColonyId ?? ""; load = identity.LoadToken ?? ""; Ledger = new NativeAttemptLedger(identity); }
        internal static bool TryGet(Common.Identity identity, [NotNullWhen(true)] out NativeOperationState? state)
        {
            state = null;
            return Current.Game != null && States.TryGetValue(Current.Game, out state)
                && state.colony == (identity.ColonyId ?? "") && state.load == (identity.LoadToken ?? "");
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
        public NativeOperationTools() { NativeDrugPolicy.Install(); NativeDesignationGuards.Install(); HomeCoverage.Install(); TreeLatticeSowing.Install(); WallLayerGuard.Install(); ConstructionTierGate.Install(); StorageRefillHysteresis.Install(); BenchCleaningGuard.Install(); RemotePickupGuard.Install(); ConstructionSkillGuard.Install(); ConsumptionHooks.Install(); SpoilagePreference.Install(); ChunkAutoHaul.Install(); TickProfile.Install(); EatTriggerGuard.Install(); NativePawnControlState.Initialize(); }

        [Tool("rimgovernor/zones_preview", Title = "Preview zone site", Description = "Check one zone intent create site as the zone Action arm would, without creating the zone.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ZonePreviewReply.", Always = true)]
        public async Task<object> PreviewZone(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations ZonePreviewRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/zones_preview", request, out Operations.ZonePreviewRequest? parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.ZonePreviewReply { Outcome = new Operations.ZonePreviewReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandlePreviewZone(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Operations.ZonePreviewReply HandlePreviewZone(Operations.ZonePreviewRequest parsed)
        {
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                return new Operations.ZonePreviewReply { Outcome = invalid };
            if (parsed.Zone == null)
                return new Operations.ZonePreviewReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A zone is required.") };
            return parsed.Zone.Kind.GetValueOrDefault() == Operations.ZoneType.Stockpile
                ? StockpilePlacementActionHandler.Preview(parsed.Zone, context) : NativeZoneCreation.Preview(parsed.Zone, context);
        }

        [Tool("rimgovernor/receipts_lookup", Title = "Look up admitted attempt", Description = "Read original admitted receipt without requiring current authority.")]
        [ToolResponse("payload", "string", "Official ProtoJSON LookupReply.", Always = true)]
        public async Task<object> Lookup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official receipts LookupRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/receipts_lookup", request, out Receipts.LookupRequest? parsed, out var failure))
                return ProtoBoundary.Encode(new Receipts.LookupReply { Outcome = new Receipts.LookupReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleLookup(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Receipts.LookupReply HandleLookup(Receipts.LookupRequest parsed)
        {
            if (!ValidAttempt(parsed.Attempt)) return new Receipts.LookupReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A complete attempt is required.") };
            if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var invalid))
                return new Receipts.LookupReply { Outcome = invalid };
            return context.Identity is { } identity && NativeOperationState.TryGet(identity, out var state)
                ? state.Ledger.Lookup(parsed.Attempt, context)
                : new Receipts.LookupReply { Outcome = new Receipts.UnknownAttempt { Context = context } };
        }

        private static bool ValidAttempt([NotNullWhen(true)] Common.AttemptKey? value) => value != null && value.ControllerSessionId is not null
            && ProtoBoundary.IsIdentifier(value.ControllerSessionId) && value.ActionId is not null && ProtoBoundary.IsIdentifier(value.ActionId)
            && value.AttemptId is not null && value.AttemptId.GetValueOrDefault() > 0;
    }
}
