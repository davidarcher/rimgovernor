package bridge

import (
	"context"
	"math"

	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// PresentationMedia wraps the two in-scope PresentationMedia RPCs for this
// slice: DemandRendering and CapturePawn. LeaseVideo/ReadFrame/AcknowledgeFrame
// and CaptureScreenshot are separate, larger follow-up slices and are not
// implemented here.
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
