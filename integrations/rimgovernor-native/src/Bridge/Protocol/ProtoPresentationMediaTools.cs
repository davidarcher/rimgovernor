#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using UnityEngine;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Presentation = RimGovernor.Protocol.Presentation;

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
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_render_state", request, Presentation.ReadRequest.Parser, out var parsed, out var failure)
                || !NativePresentationReadTools.ValidateRead(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.RenderReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.RenderReply { Failure = error });
                return ProtoBoundary.Encode(new Presentation.RenderReply { Status = Status(context, RenderDemandDriver.Peek()) });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/presentation_render_demand", Title = "Demand native rendering", Description = "Controller rendering lease. No simulation or game-speed changes. Visible game window always renders. PlayerIdentity's direction/viewer fields are informational only; they authenticate nothing.")]
        [ToolResponse("payload", "string", "Official ProtoJSON RenderReply.", Always = true)]
        public async Task<object> DemandRendering(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RenderDemand string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_render_demand", request, Presentation.RenderDemand.Parser, out var parsed, out var failure)
                || !ValidateDemand(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.RenderReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateViewedIdentity(parsed.Viewer.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.RenderReply { Failure = error });
                var status = RenderDemandDriver.Lease((int)parsed.LeaseSeconds);
                return ProtoBoundary.Encode(new Presentation.RenderReply { Status = Status(context, status) });
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Presentation.RenderStatus Status(Common.ObservationContext context, RenderDemandStatus status)
        {
            var result = new Presentation.RenderStatus { Context = context, Supported = status.Supported };
            if (!status.Supported)
            {
                result.Suspended = true;
                result.Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.Unsupported,
                    Detail = string.IsNullOrEmpty(status.UnavailableDetail) ? "Native rendering is unsupported." : status.UnavailableDetail };
                return result;
            }
            result.Suspended = status.Suspended;
            result.WindowVisible = status.WindowVisible;
            result.RemainingLeaseMs = (uint)Math.Max(0, Mathf.RoundToInt(status.RemainingSeconds * 1000f));
            return result;
        }

        private static bool ValidateDemand(Presentation.RenderDemand request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact viewer identity and a lease of 0-30 seconds are required.");
            if (request?.Viewer?.Identity == null) return false;
            if (request.LeaseSeconds > 30) return false;
            if (request.Viewer.HasViewerId && !ProtoBoundary.IsIdentifier(request.Viewer.ViewerId)) return false;
            return true;
        }
    }
}
