package bridge

import (
	"context"

	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// PresentationMedia wraps PresentationMedia.DemandRendering, the controller
// rendering lease. CaptureScreenshot is not implemented here.
type PresentationMedia struct{ client *Client }

func NewPresentationMedia(client *Client) (*PresentationMedia, error) {
	if client == nil {
		return nil, contract("presentation media capability required")
	}
	return &PresentationMedia{client}, nil
}

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
