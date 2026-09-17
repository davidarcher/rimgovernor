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
    // Typed-proto surface for PresentationMedia's DemandRendering/CapturePawn,
    // the video streaming trio (LeaseVideo/ReadFrame/AcknowledgeFrame) and
    // PresentationReads' RenderState. CaptureScreenshot and the whole
    // PlayerPresentation service (camera/input ownership) remain out of scope.
    // All drivers (RenderDemandDriver, PawnImageCapture, VideoStreamDriver) are
    // the same ones their legacy home/* tools use; only one Harmony patch
    // registration exists per driver. ReadFrame reads VideoStreamDriver's
    // latest in-process captured bytes directly; it never reopens the legacy
    // mmap buffer, which stays reserved for home/video_stream's own consumer.
    public sealed class ProtoPresentationMediaTools
    {
        [Tool("rimgovernor/presentation_render_state", Title = "Read native rendering lease state", Description = "Zero-side-effect observation of the controller rendering lease. Never extends or shortens the lease; DemandRendering is the only RPC that changes it.")]
        [ToolResponse("payload", "string", "Official ProtoJSON RenderReply.", Always = true)]
        public async Task<object> RenderState(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ReadRequest string in raw transport value.")] object request = null!)
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
            [ToolParameter(Description = "Official ProtoJSON RenderDemand string in raw transport value.")] object request = null!)
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
                    if (!ProtoBoundary.ValidateViewedIdentity(parsed.Identity, out var context, out var error))
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

        [Tool("rimgovernor/presentation_lease_video", Title = "Lease native video capture", Description = "Starts or stops the shared in-process video capture ReadFrame reads from. No simulation or game-speed changes; PlayerIdentity's direction/viewer fields authenticate nothing.")]
        [ToolResponse("payload", "string", "Official ProtoJSON VideoReply.", Always = true)]
        public async Task<object> LeaseVideo(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON VideoLeaseRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_lease_video", request, Presentation.VideoLeaseRequest.Parser, out var parsed, out var failure)
                || !ValidateVideoLease(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.VideoReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                var viewer = parsed.OperationCase == Presentation.VideoLeaseRequest.OperationOneofCase.Start ? parsed.Start.Viewer : parsed.Stop.Viewer;
                if (!ProtoBoundary.ValidateViewedIdentity(viewer.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.VideoReply { Failure = error });
                var spec = VideoSourceSpec.Screen;
                int seconds = 0;
                string? stopSource = null;
                if (parsed.OperationCase == Presentation.VideoLeaseRequest.OperationOneofCase.Start)
                {
                    seconds = (int)parsed.Start.LeaseSeconds;
                    if (!TryParseSource(parsed.Start.Source, out spec, out var reason))
                        return ProtoBoundary.Encode(new Presentation.VideoReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, reason) });
                }
                else stopSource = parsed.Stop.HasSourceId ? parsed.Stop.SourceId : null;
                var status = VideoStreamDriver.LeaseTyped(seconds, spec, stopSource);
                return ProtoBoundary.Encode(new Presentation.VideoReply { State = VideoStatus(context, status) });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/presentation_read_frame", Title = "Read latest captured video frame", Description = "Returns the most recently captured raw video frame for the current lease. Never extends or shortens the lease; LeaseVideo is the only RPC that changes it.")]
        [ToolResponse("payload", "string", "Official ProtoJSON FrameReply.", Always = true)]
        public async Task<object> ReadFrame(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON FrameRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_read_frame", request, Presentation.FrameRequest.Parser, out var parsed, out var failure)
                || !ValidateFrameRequest(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.FrameReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateViewedIdentity(parsed.Viewer.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.FrameReply { Failure = error });
                if (!VideoStreamDriver.TryReadLatestFrame(parsed.HasSourceId ? parsed.SourceId : null, out var snapshot))
                    return ProtoBoundary.Encode(new Presentation.FrameReply { Failure = Unavailable("No active video capture or captured frame is available yet.") });
                var frame = new Presentation.MediaFrame
                {
                    Frame = new Presentation.FrameReference { SourceId = snapshot.SourceId, Sequence = (ulong)snapshot.Sequence },
                    Width = (uint)snapshot.Width,
                    Height = (uint)snapshot.Height,
                    Encoding = snapshot.Bgra ? Presentation.MediaEncoding.Bgra32TopDown : Presentation.MediaEncoding.Rgba32BottomUp,
                    CaptureMethod = snapshot.CaptureMethod switch
                    {
                        "private-presented-window" => Presentation.CaptureMethod.PrivatePresentedWindow,
                        "async-gpu" => Presentation.CaptureMethod.AsyncGpu,
                        _ => Presentation.CaptureMethod.ReadPixels,
                    },
                    CapturedUnixMs = (long)(snapshot.CapturedUnixSeconds * 1000.0),
                    ReadbackMs = snapshot.ReadbackMs,
                    Data = ByteString.CopyFrom(snapshot.Data),
                };
                return EncodeMedia(new Presentation.FrameReply { Frame = frame });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/presentation_acknowledge_frame", Title = "Acknowledge a displayed video frame", Description = "Records that a viewer displayed a given frame reference. Accept-and-record telemetry only; it does not yet throttle capture to acknowledged consumption.")]
        [ToolResponse("payload", "string", "Official ProtoJSON FrameAcknowledgementReply.", Always = true)]
        public async Task<object> AcknowledgeFrame(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON FrameAcknowledgement string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_acknowledge_frame", request, Presentation.FrameAcknowledgement.Parser, out var parsed, out var failure)
                || !ValidateFrameAcknowledgement(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.FrameAcknowledgementReply { Refusal = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateViewedIdentity(parsed.Viewer.Identity, out _, out var error))
                    return ProtoBoundary.Encode(new Presentation.FrameAcknowledgementReply { Refusal = error });
                return ProtoBoundary.Encode(new Presentation.FrameAcknowledgementReply
                {
                    Acknowledged = new Presentation.FrameAcknowledged { Frame = parsed.Frame },
                });
            }, cancellationToken).ConfigureAwait(false);
        }

        // Absent source means the presented screen, as before sources existed.
        private static bool TryParseSource(Presentation.VideoSource? source, out VideoSourceSpec spec, out string? reason)
        {
            var kind = source?.Kind ?? Presentation.VideoSourceKind.Screen;
            VideoSourceKind native;
            switch (kind)
            {
                case Presentation.VideoSourceKind.Unspecified:
                case Presentation.VideoSourceKind.Screen: native = VideoSourceKind.Screen; break;
                case Presentation.VideoSourceKind.Pawn: native = VideoSourceKind.Pawn; break;
                case Presentation.VideoSourceKind.Map: native = VideoSourceKind.Map; break;
                default: spec = VideoSourceSpec.Screen; reason = "Unsupported video source kind."; return false;
            }
            if (native == VideoSourceKind.Pawn && (source == null || !source.HasPawnId || !ProtoBoundary.IsIdentifier(source.PawnId)))
            { spec = VideoSourceSpec.Screen; reason = "A pawn source requires a valid pawn_id."; return false; }
            return VideoSourceSpec.TryCreate(native, source?.PawnId, (int)(source?.Width ?? 0), (int)(source?.Height ?? 0),
                source?.FramesPerSecond ?? 0, out spec, out reason);
        }

        private static Presentation.VideoSource WireSource(VideoSourceSpec spec)
        {
            var result = new Presentation.VideoSource
            {
                Kind = spec.Kind == VideoSourceKind.Map ? Presentation.VideoSourceKind.Map
                    : spec.Kind == VideoSourceKind.Pawn ? Presentation.VideoSourceKind.Pawn : Presentation.VideoSourceKind.Screen,
                FramesPerSecond = spec.FramesPerSecond,
            };
            if (spec.PawnId != null) result.PawnId = spec.PawnId;
            if (spec.Width > 0) result.Width = (uint)spec.Width;
            if (spec.Height > 0) result.Height = (uint)spec.Height;
            return result;
        }

        private static Presentation.VideoState VideoStatus(Common.ObservationContext context, VideoLeaseStatus status)
        {
            var result = new Presentation.VideoState { Context = context, Supported = status.Supported, Source = WireSource(status.Source) };
            if (!status.Supported)
            {
                result.Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.Unsupported,
                    Detail = string.IsNullOrEmpty(status.UnavailableDetail) ? "Native video capture is unsupported." : status.UnavailableDetail };
                return result;
            }
            result.Active = status.Active;
            if (status.Active)
            {
                result.SourceId = status.SourceId;
                result.RemainingLeaseMs = (uint)Math.Max(0, Mathf.RoundToInt(status.RemainingSeconds * 1000f));
                result.CapturedFrames = (ulong)Math.Max(0, status.CapturedFrames);
                result.FramesPerSecond = status.Source.FramesPerSecond;
                result.PixelFormat = status.Bgra ? Presentation.MediaEncoding.Bgra32TopDown : Presentation.MediaEncoding.Rgba32BottomUp;
                result.CaptureMethod = status.CaptureMethod switch
                {
                    "private-presented-window" => Presentation.CaptureMethod.PrivatePresentedWindow,
                    "async-gpu" => Presentation.CaptureMethod.AsyncGpu,
                    _ => Presentation.CaptureMethod.ReadPixels,
                };
            }
            result.AsyncReadbackSupported = status.AsyncReadbackSupported;
            result.Renderer = status.Renderer;
            result.Focused = status.Focused;
            result.TargetFrameRate = status.TargetFrameRate;
            result.FrameSeconds = status.FrameSeconds;
            result.VsyncCount = status.VsyncCount;
            result.RefreshRate = status.RefreshRate;
            result.WorkingSetBytes = status.WorkingSetBytes;
            result.ProcessCpuSeconds = status.ProcessCpuSeconds;
            return result;
        }

        private static bool ValidateVideoLease(Presentation.VideoLeaseRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact viewer identity and, for Start, a lease of 0-15 seconds are required.");
            switch (request?.OperationCase)
            {
                case Presentation.VideoLeaseRequest.OperationOneofCase.Start:
                    return request.Start.Viewer?.Identity != null && request.Start.LeaseSeconds <= 15
                        && (!request.Start.Viewer.HasViewerId || ProtoBoundary.IsIdentifier(request.Start.Viewer.ViewerId));
                case Presentation.VideoLeaseRequest.OperationOneofCase.Stop:
                    return request.Stop.Viewer?.Identity != null
                        && (!request.Stop.Viewer.HasViewerId || ProtoBoundary.IsIdentifier(request.Stop.Viewer.ViewerId))
                        && (!request.Stop.HasSourceId || ProtoBoundary.IsIdentifier(request.Stop.SourceId));
                default:
                    return false;
            }
        }

        private static bool ValidateFrameRequest(Presentation.FrameRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact viewer identity is required.");
            return request?.Viewer?.Identity != null
                && (!request.Viewer.HasViewerId || ProtoBoundary.IsIdentifier(request.Viewer.ViewerId))
                && (!request.HasSourceId || ProtoBoundary.IsIdentifier(request.SourceId));
        }

        private static bool ValidateFrameAcknowledgement(Presentation.FrameAcknowledgement request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact viewer identity and a frame reference are required.");
            return request?.Viewer?.Identity != null && request.Frame != null
                && ProtoBoundary.IsIdentifier(request.Frame.SourceId) && request.Frame.Sequence > 0
                && (!request.Viewer.HasViewerId || ProtoBoundary.IsIdentifier(request.Viewer.ViewerId));
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
