package bridge

import (
	"context"
	"math"

	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// PresentationMedia wraps the in-scope PresentationMedia RPCs: DemandRendering,
// CapturePawn and the video streaming trio (LeaseVideo/ReadFrame/AcknowledgeFrame).
// CaptureScreenshot is a separate, larger follow-up slice and is not implemented here.
type PresentationMedia struct{ client *Client }

func NewPresentationMedia(client *Client) (*PresentationMedia, error) {
	if client == nil {
		return nil, contract("presentation media capability required")
	}
	return &PresentationMedia{client}, nil
}

const maxPawnImageBytes = 8 << 20 // generous single-frame bound, well under the 32 MiB proto ceiling

// DemandRendering asks the native mod to keep rendering for lease_seconds real
// seconds; zero only reads status without changing it. PlayerIdentity carries
// no authority of its own here: PlayerPresentation (input/camera ownership) is
// out of scope for this slice, so player_direction/viewer_id are informational
// only and are never checked against any native authority component.
func (m *PresentationMedia) DemandRendering(ctx context.Context, request *p.RenderDemand) (*p.RenderReply, Result, error) {
	if m == nil || m.client == nil {
		return nil, Result{}, contract("presentation media capability required")
	}
	if request == nil || request.Viewer == nil {
		return nil, Result{}, contract("render demand viewer required")
	}
	if err := validatePlayerIdentity(request.Viewer); err != nil {
		return nil, Result{}, err
	}
	if request.GetLeaseSeconds() > 30 {
		return nil, Result{}, contract("render demand lease exceeds 30 seconds")
	}
	request = proto.Clone(request).(*p.RenderDemand)
	reply := &p.RenderReply{}
	raw, err := m.client.protoCall(ctx, "rimgovernor/presentation_render_demand", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.RenderReply_Failure:
		return reply, raw, failure(v.Failure, raw)
	case *p.RenderReply_Status:
		err = validateRenderStatus(v.Status, request.Viewer.Identity)
	default:
		err = contract("render demand outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}

// CapturePawn renders a colonist portrait or a temporary follow view and
// returns a single PNG MediaFrame. It is an active native capture (a real
// draw-cycle round trip), not a free read.
func (m *PresentationMedia) CapturePawn(ctx context.Context, request *p.PawnImageRequest) (*p.PawnImageReply, Result, error) {
	if m == nil || m.client == nil {
		return nil, Result{}, contract("presentation media capability required")
	}
	if err := validatePawnImageRequest(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*p.PawnImageRequest)
	reply := &p.PawnImageReply{}
	raw, err := m.client.protoCall(ctx, "rimgovernor/presentation_capture_pawn", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.PawnImageReply_Failure:
		return reply, raw, failure(v.Failure, raw)
	case *p.PawnImageReply_Image:
		err = validatePawnImage(v.Image, request)
	default:
		err = contract("pawn image outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}

// maxVideoFrameBytes bounds a raw uncompressed 3840x2160 RGBA/BGRA32 frame
// (matches VideoStreamTool's native buffer capacity), well under the 48 MiB
// media envelope.
const maxVideoFrameBytes = 3840 * 2160 * 4

// LeaseVideo starts or stops the shared in-process video capture used by
// ReadFrame. It mirrors the legacy home/video_stream Lease(seconds) semantics
// (0-15 real seconds; 0 stops) but returns the richer typed VideoState.
func (m *PresentationMedia) LeaseVideo(ctx context.Context, request *p.VideoLeaseRequest) (*p.VideoReply, Result, error) {
	if m == nil || m.client == nil {
		return nil, Result{}, contract("presentation media capability required")
	}
	if err := validateVideoLeaseRequest(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*p.VideoLeaseRequest)
	reply := &p.VideoReply{}
	raw, err := m.client.protoCall(ctx, "rimgovernor/presentation_lease_video", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.VideoReply_Failure:
		return reply, raw, failure(v.Failure, raw)
	case *p.VideoReply_State:
		err = validateVideoState(v.State)
	default:
		err = contract("video lease outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}

// ReadFrame returns the most recently captured raw video frame for the
// current lease. It never itself extends or shortens the lease.
func (m *PresentationMedia) ReadFrame(ctx context.Context, request *p.FrameRequest) (*p.FrameReply, Result, error) {
	if m == nil || m.client == nil {
		return nil, Result{}, contract("presentation media capability required")
	}
	if request == nil || request.Viewer == nil {
		return nil, Result{}, contract("frame request viewer required")
	}
	if err := validatePlayerIdentity(request.Viewer); err != nil {
		return nil, Result{}, err
	}
	if request.SourceId != nil && validID(request.GetSourceId()) != nil {
		return nil, Result{}, contract("invalid frame request source id")
	}
	request = proto.Clone(request).(*p.FrameRequest)
	reply := &p.FrameReply{}
	raw, err := m.client.protoCall(ctx, "rimgovernor/presentation_read_frame", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.FrameReply_Failure:
		return reply, raw, failure(v.Failure, raw)
	case *p.FrameReply_Frame:
		err = validateVideoFrame(v.Frame)
	default:
		err = contract("frame outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}

// AcknowledgeFrame records that a viewer displayed a given frame. This slice
// only accepts and records the acknowledgement; throttling capture to
// acknowledged consumption is a future refinement.
func (m *PresentationMedia) AcknowledgeFrame(ctx context.Context, request *p.FrameAcknowledgement) (*p.FrameAcknowledgementReply, Result, error) {
	if m == nil || m.client == nil {
		return nil, Result{}, contract("presentation media capability required")
	}
	if err := validateFrameAcknowledgement(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*p.FrameAcknowledgement)
	reply := &p.FrameAcknowledgementReply{}
	raw, err := m.client.protoCall(ctx, "rimgovernor/presentation_acknowledge_frame", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.FrameAcknowledgementReply_Refusal:
		return reply, raw, failure(v.Refusal, raw)
	case *p.FrameAcknowledgementReply_Acknowledged:
		if v.Acknowledged == nil || !sameFrameReference(v.Acknowledged.Frame, request.Frame) {
			err = contract("acknowledged frame reference mismatch")
		}
	default:
		err = contract("frame acknowledgement outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}

func validateVideoLeaseRequest(v *p.VideoLeaseRequest) error {
	if v == nil {
		return contract("video lease request required")
	}
	switch op := v.Operation.(type) {
	case *p.VideoLeaseRequest_Start:
		if op.Start == nil || op.Start.Viewer == nil {
			return contract("video lease start viewer required")
		}
		if err := validatePlayerIdentity(op.Start.Viewer); err != nil {
			return err
		}
		if op.Start.GetLeaseSeconds() > 15 {
			return contract("video lease exceeds 15 seconds")
		}
	case *p.VideoLeaseRequest_Stop:
		if op.Stop == nil || op.Stop.Viewer == nil {
			return contract("video lease stop viewer required")
		}
		if err := validatePlayerIdentity(op.Stop.Viewer); err != nil {
			return err
		}
		if op.Stop.SourceId != nil && validID(op.Stop.GetSourceId()) != nil {
			return contract("invalid video lease stop source id")
		}
	default:
		return contract("exact video lease operation required")
	}
	return nil
}
func validateFrameAcknowledgement(v *p.FrameAcknowledgement) error {
	if v == nil || v.Viewer == nil || v.Frame == nil {
		return contract("frame acknowledgement viewer and frame required")
	}
	if err := validatePlayerIdentity(v.Viewer); err != nil {
		return err
	}
	if err := validateFrameReference(v.Frame); err != nil {
		return err
	}
	if v.SelectionEffectMs != nil {
		value := v.GetSelectionEffectMs()
		if math.IsNaN(value) || math.IsInf(value, 0) || value < 0 {
			return contract("invalid frame acknowledgement selection effect")
		}
	}
	return nil
}
func validateFrameReference(v *p.FrameReference) error {
	if v == nil || v.SourceId == nil || validID(v.GetSourceId()) != nil || v.Sequence == nil || v.GetSequence() == 0 {
		return contract("invalid frame reference")
	}
	return nil
}
func sameFrameReference(a, b *p.FrameReference) bool {
	if a == nil || b == nil {
		return false
	}
	return a.GetSourceId() == b.GetSourceId() && a.GetSequence() == b.GetSequence()
}
func validateVideoState(v *p.VideoState) error {
	if v == nil {
		return contract("video state missing")
	}
	if v.Unavailable != nil {
		return validateUnavailable(v.Unavailable)
	}
	if v.GetActive() {
		if v.SourceId == nil || validID(v.GetSourceId()) != nil {
			return contract("active video lease requires source id")
		}
	}
	if v.FramesPerSecond != nil {
		value := v.GetFramesPerSecond()
		if math.IsNaN(value) || math.IsInf(value, 0) || value < 0 {
			return contract("invalid video frames per second")
		}
	}
	return nil
}
func validateVideoFrame(v *p.MediaFrame) error {
	if v == nil {
		return contract("video frame missing")
	}
	if err := validateFrameReference(v.Frame); err != nil {
		return err
	}
	if v.Width == nil || v.Height == nil || v.GetWidth() == 0 || v.GetHeight() == 0 || v.GetWidth() > 3840 || v.GetHeight() > 2160 {
		return contract("invalid video frame dimensions")
	}
	switch v.GetEncoding() {
	case p.MediaEncoding_MEDIA_ENCODING_RGBA32_BOTTOM_UP, p.MediaEncoding_MEDIA_ENCODING_BGRA32_TOP_DOWN:
	default:
		return contract("video frame must be raw RGBA32 or BGRA32")
	}
	switch v.GetCaptureMethod() {
	case p.CaptureMethod_CAPTURE_METHOD_PRIVATE_PRESENTED_WINDOW, p.CaptureMethod_CAPTURE_METHOD_ASYNC_GPU, p.CaptureMethod_CAPTURE_METHOD_READ_PIXELS:
	default:
		return contract("unexpected video frame capture method")
	}
	if v.CapturedUnixMs == nil || v.GetCapturedUnixMs() <= 0 {
		return contract("invalid video frame capture timestamp")
	}
	if v.ReadbackMs == nil || math.IsNaN(v.GetReadbackMs()) || math.IsInf(v.GetReadbackMs(), 0) || v.GetReadbackMs() < 0 {
		return contract("invalid video frame readback duration")
	}
	if len(v.Data) == 0 || len(v.Data) > maxVideoFrameBytes {
		return contract("invalid video frame data size")
	}
	return nil
}
func validatePlayerIdentity(v *p.PlayerIdentity) error {
	if v == nil {
		return contract("player identity missing")
	}
	if err := ValidateIdentity(v.Identity); err != nil {
		return err
	}
	if v.ViewerId != nil && validID(v.GetViewerId()) != nil {
		return contract("invalid viewer id")
	}
	return nil
}
func validatePawnImageRequest(v *p.PawnImageRequest) error {
	if v == nil {
		return contract("pawn image request required")
	}
	if err := ValidateIdentity(v.Identity); err != nil {
		return err
	}
	if v.PawnId == nil || validID(v.GetPawnId()) != nil {
		return contract("pawn id required")
	}
	if v.View == nil || (v.GetView() != p.PawnView_PAWN_VIEW_PORTRAIT && v.GetView() != p.PawnView_PAWN_VIEW_FOLLOW) {
		return contract("exact pawn view required")
	}
	return nil
}
func validatePawnImage(v *p.PawnImage, q *p.PawnImageRequest) error {
	if v == nil {
		return contract("pawn image missing")
	}
	if v.PawnId == nil || v.GetPawnId() != q.GetPawnId() {
		return contract("pawn image id mismatch")
	}
	if v.View == nil || v.GetView() != q.GetView() {
		return contract("pawn image view mismatch")
	}
	return validateMediaFrame(v.Frame, q.GetView())
}
func validateMediaFrame(v *p.MediaFrame, view p.PawnView) error {
	if v == nil {
		return contract("media frame missing")
	}
	if v.Width == nil || v.Height == nil || v.GetWidth() == 0 || v.GetHeight() == 0 || v.GetWidth() > 8192 || v.GetHeight() > 8192 {
		return contract("invalid media frame dimensions")
	}
	if v.Encoding == nil || v.GetEncoding() != p.MediaEncoding_MEDIA_ENCODING_PNG {
		return contract("pawn image must be PNG")
	}
	wantMethod := p.CaptureMethod_CAPTURE_METHOD_PORTRAIT
	if view == p.PawnView_PAWN_VIEW_FOLLOW {
		wantMethod = p.CaptureMethod_CAPTURE_METHOD_OFFSCREEN_FOLLOW
	}
	if v.CaptureMethod == nil || v.GetCaptureMethod() != wantMethod {
		return contract("unexpected pawn image capture method")
	}
	if v.CapturedUnixMs == nil || v.GetCapturedUnixMs() <= 0 {
		return contract("invalid pawn image capture timestamp")
	}
	if v.ReadbackMs == nil || math.IsNaN(v.GetReadbackMs()) || math.IsInf(v.GetReadbackMs(), 0) || v.GetReadbackMs() < 0 {
		return contract("invalid pawn image readback duration")
	}
	if len(v.Data) == 0 || len(v.Data) > maxPawnImageBytes {
		return contract("invalid pawn image data size")
	}
	// PNG signature; the native encoder always uses ImageConversion.EncodeToPNG.
	if len(v.Data) < 8 || v.Data[0] != 0x89 || v.Data[1] != 'P' || v.Data[2] != 'N' || v.Data[3] != 'G' {
		return contract("pawn image data is not PNG")
	}
	return nil
}
