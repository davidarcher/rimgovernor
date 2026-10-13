#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using UnityEngine;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Presentation = RimGovernor.Contracts.Wire.Presentation;

namespace HomeBridge.BridgeTools
{
    // Typed-proto surface for PresentationMedia's DemandRendering and
    // PresentationReads' RenderState, both over RenderDemandDriver.
    public sealed class ProtoPresentationMediaTools
    {
        [Tool("rimgovernor/presentation_render_state", Title = "Read native rendering lease state", Description = "Zero-side-effect observation of the controller rendering lease. Never extends or shortens the lease; DemandRendering is the only RPC that changes it.")]
        [ToolResponse("payload", "string", "Official ProtoJSON RenderReply.", Always = true)]
        public async Task<object> RenderState(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ReadRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Presentation.ReadRequest>(ctx, "rimgovernor/presentation_render_state", request, out var parsed, out var failure)
                || !NativePresentationReadTools.ValidateRead(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.RenderReply { Outcome = new Presentation.RenderReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleRenderState(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool.
        internal static Presentation.RenderReply HandleRenderState(Presentation.ReadRequest parsed)
        {
            if (!NativePresentationReadTools.ValidateRead(parsed, out var failure0)) return new Presentation.RenderReply { Outcome = new Presentation.RenderReplyOutcome.FailureCase(failure0) };
            if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                return new Presentation.RenderReply { Outcome = error };
            return new Presentation.RenderReply { Outcome = Status(context, RenderDemandDriver.Peek()) };
        }

        [Tool("rimgovernor/presentation_render_demand", Title = "Demand native rendering", Description = "Controller rendering lease. No simulation or game-speed changes. Visible game window always renders. PlayerIdentity's direction/viewer fields are informational only; they authenticate nothing.")]
        [ToolResponse("payload", "string", "Official ProtoJSON RenderReply.", Always = true)]
        public async Task<object> DemandRendering(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RenderDemand string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Presentation.RenderDemand>(ctx, "rimgovernor/presentation_render_demand", request, out var parsed, out var failure)
                || !ValidateDemand(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.RenderReply { Outcome = new Presentation.RenderReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleDemandRendering(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool.
        internal static Presentation.RenderReply HandleDemandRendering(Presentation.RenderDemand parsed)
        {
            if (!ValidateDemand(parsed, out var failure0)) return new Presentation.RenderReply { Outcome = new Presentation.RenderReplyOutcome.FailureCase(failure0) };
            if (!ProtoBoundary.ValidateViewedIdentity(parsed?.Viewer?.Identity, out var context, out var error))
                return new Presentation.RenderReply { Outcome = error };
            var status = RenderDemandDriver.Lease((int)(parsed?.LeaseSeconds).GetValueOrDefault());
            return new Presentation.RenderReply { Outcome = Status(context, status) };
        }

        private static Presentation.RenderStatus Status(Common.ObservationContext context, RenderDemandStatus status)
        {
            var result = new Presentation.RenderStatus { Context = context, Supported = status.Supported };
            if (!status.Supported)
            {
                result = result with { Suspended = true };
                result = result with { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.Unsupported,
                    Detail = string.IsNullOrEmpty(status.UnavailableDetail) ? "Native rendering is unsupported." : status.UnavailableDetail } };
                return result;
            }
            result = result with { Suspended = status.Suspended };
            result = result with { WindowVisible = status.WindowVisible };
            result = result with { RemainingLeaseMs = (uint)Math.Max(0, Mathf.RoundToInt(status.RemainingSeconds * 1000f)) };
            return result;
        }

        private static bool ValidateDemand(Presentation.RenderDemand request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact viewer identity and a lease of 0-30 seconds are required.");
            if (request?.Viewer?.Identity == null) return false;
            if (request.LeaseSeconds.GetValueOrDefault() > 30) return false;
            if (request.Viewer.ViewerId is not null && !ProtoBoundary.IsIdentifier(request.Viewer.ViewerId)) return false;
            return true;
        }
    }
}
