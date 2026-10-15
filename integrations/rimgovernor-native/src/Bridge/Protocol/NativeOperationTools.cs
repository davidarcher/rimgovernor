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
    public sealed class NativeOperationTools
    {
        public NativeOperationTools() { NativeDrugPolicy.Install(); NativeDesignationGuards.Install(); HomeCoverage.Install(); TreeLatticeSowing.Install(); WallLayerGuard.Install(); ConstructionTierGate.Install(); RemotePickupGuard.Install(); ConstructionSkillGuard.Install(); ConsumptionHooks.Install(); SpoilagePreference.Install(); TickProfile.Install(); NativePawnControlState.Initialize(); NativePawnActivities.Install(); }

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
            // Native keeps no attempt ledger; an unknown attempt proves no absence of effects.
            return new Receipts.LookupReply { Outcome = new Receipts.UnknownAttempt { Context = context } };
        }

        private static bool ValidAttempt([NotNullWhen(true)] Common.AttemptKey? value) => value != null && value.ControllerSessionId is not null
            && ProtoBoundary.IsIdentifier(value.ControllerSessionId) && value.ActionId is not null && ProtoBoundary.IsIdentifier(value.ActionId)
            && value.AttemptId is not null && value.AttemptId.GetValueOrDefault() > 0;
    }
}
