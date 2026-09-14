#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using UnityEngine;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Presentation = RimGovernor.Protocol.Presentation;

namespace HomeBridge.BridgeTools
{
    // Typed-proto surface for PresentationMedia's DemandRendering/CapturePawn and
    // PresentationReads' RenderState. LeaseVideo/ReadFrame/AcknowledgeFrame,
    // CaptureScreenshot and the whole PlayerPresentation service (camera/input
    // ownership) are out of scope for this slice and are not implemented here.
    // Both drivers (RenderDemandDriver, PawnImageCapture) are the same ones the
    // legacy home/render_demand and home/pawn_image tools use; only one Harmony
    // patch registration exists per driver.
    public sealed class ProtoPresentationMediaTools
    {
        [Tool("rimgovernor/presentation_render_state", Title = "Read native rendering lease state", Description = "Zero-side-effect observation of the controller rendering lease. Never extends or shortens the lease; DemandRendering is the only RPC that changes it.")]
        [ToolResponse("payload", "string", "Official ProtoJSON RenderReply.", Always = true)]
        public async Task<object> RenderState(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ReadRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_render_state", request, Presentation.ReadRequest.Parser, out var parsed, out var failure)
                || !NativePresentationReadTools.ValidateRead(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.RenderReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, Find.CurrentMap, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.RenderReply { Failure = error });
                return ProtoBoundary.Encode(new Presentation.RenderReply { Status = Status(context, RenderDemandDriver.Peek()) });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/presentation_render_demand", Title = "Demand native rendering", Description = "Controller rendering lease. No simulation or game-speed changes. Visible game window always renders. PlayerIdentity's direction/viewer fields are informational only; they authenticate nothing.")]
        [ToolResponse("payload", "string", "Official ProtoJSON RenderReply.", Always = true)]
        public async Task<object> DemandRendering(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON RenderDemand string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_render_demand", request, Presentation.RenderDemand.Parser, out var parsed, out var failure)
                || !ValidateDemand(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.RenderReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Viewer.Identity, Find.CurrentMap, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.RenderReply { Failure = error });
                var status = RenderDemandDriver.Lease((int)parsed.LeaseSeconds);
                return ProtoBoundary.Encode(new Presentation.RenderReply { Status = Status(context, status) });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/presentation_capture_pawn", Title = "Capture native colonist image", Description = "Read-only native colonist portrait or independent nearby map image. No selection, orders, clock or player camera navigation. Batch-mode or no-camera games return a typed failure, not an error.")]
        [ToolResponse("payload", "string", "Official ProtoJSON PawnImageReply.", Always = true)]
        public async Task<object> CapturePawn(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON PawnImageRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_capture_pawn", request, Presentation.PawnImageRequest.Parser, out var parsed, out var failure)
                || !ValidateCapture(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.PawnImageReply { Failure = failure });

            Common.Failure? beginError = null;
            Task<PawnCaptureResult>? pending;
            try
            {
                pending = await ctx.MainThread.InvokeAsync(() => {
                    if (!ProtoBoundary.ValidateIdentity(parsed.Identity, Find.CurrentMap, out var context, out var error))
                    { beginError = error; return null; }
                    if (Application.isBatchMode || Find.Camera == null)
                    { beginError = Unavailable("Pawn images require a rendered game."); return null; }
                    var sessionId = context.Identity.ColonyId + ":" + context.Identity.MapId + ":" + context.Identity.LoadToken;
                    var view = parsed.View == Presentation.PawnView.Follow ? "follow" : "portrait";
                    return PawnImageCapture.Begin(parsed.PawnId, sessionId, view);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException errorBegin)
            {
                // A stale viewer or busy capture pipeline is an ordinary refusal, not a game attention event.
                return ProtoBoundary.Encode(new Presentation.PawnImageReply { Failure = Unavailable(errorBegin.Message) });
            }
            if (pending == null) return ProtoBoundary.Encode(new Presentation.PawnImageReply { Failure = beginError });

            try
            {
                var result = await pending.ConfigureAwait(false);
                var frame = new Presentation.MediaFrame
                {
                    Width = (uint)result.Width,
                    Height = (uint)result.Height,
                    Encoding = Presentation.MediaEncoding.Png,
                    CaptureMethod = result.View == "follow" ? Presentation.CaptureMethod.OffscreenFollow : Presentation.CaptureMethod.Portrait,
                    CapturedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ReadbackMs = result.ReadbackMs,
                    Data = ByteString.CopyFrom(result.Png),
                };
                var image = new Presentation.PawnImage { PawnId = result.PawnId, View = parsed.View, Frame = frame };
                return EncodeMedia(new Presentation.PawnImageReply { Image = image });
            }
            catch (InvalidOperationException errorCapture)
            {
                return ProtoBoundary.Encode(new Presentation.PawnImageReply { Failure = Unavailable(errorCapture.Message) });
            }
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

        private static bool ValidateCapture(Presentation.PawnImageRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity, pawn id and view are required.");
            return request?.Identity != null && ProtoBoundary.IsIdentifier(request.PawnId)
                && (request.View == Presentation.PawnView.Portrait || request.View == Presentation.PawnView.Follow);
        }

        private static Common.Failure Unavailable(string detail) => ProtoBoundary.Fail(Common.FailureCode.Unavailable, detail);
        private static object EncodeMedia(IMessage reply) => ProtoBoundary.EncodeMedia(reply);
    }
}
