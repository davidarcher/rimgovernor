package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// DrawOverlay replaces the named overlay layer: the native drops
// layerID's shapes and labels and draws the overlay's; enabled=false only
// removes the layer. Output only, never read back or persisted.
func (client *Client) DrawOverlay(ctx context.Context, identity *c.Identity, layerID string, overlay policy.LayoutOverlay, enabled bool) (*p.OverlayApplied, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &p.OverlayRequest{Identity: proto.Clone(identity).(*c.Identity), LayerId: proto.String(layerID), Enabled: proto.Bool(enabled)}
	if enabled {
		request.Shapes, request.Labels = overlayRequest(overlay)
	}
	reply := &p.OverlayReply{}
	raw, err := client.protoCall(ctx, "rimgovernor/presentation_overlay", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.OverlayReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	case *p.OverlayReply_Applied:
		if v.Applied.GetContext() == nil {
			return nil, raw, contract("overlay context required")
		}
		return v.Applied, raw, nil
	}
	return nil, raw, contract("overlay outcome required")
}

func overlayRequest(overlay policy.LayoutOverlay) ([]*p.OverlayShape, []*p.OverlayLabel) {
	shapes := make([]*p.OverlayShape, 0, len(overlay.Layers))
	for _, l := range overlay.Layers {
		shape := &p.OverlayShape{
			Color: &p.OverlayColor{R: proto.Float32(l.Color.R), G: proto.Float32(l.Color.G), B: proto.Float32(l.Color.B), A: proto.Float32(l.Color.A)},
			Style: p.OverlayStyle_OVERLAY_STYLE_FILL,
		}
		if l.Style == policy.OverlayOutline {
			shape.Style = p.OverlayStyle_OVERLAY_STYLE_OUTLINE
		}
		for _, r := range l.Rects {
			shape.Rects = append(shape.Rects, &p.MapRect{MinX: proto.Int32(r.X), MinZ: proto.Int32(r.Z), MaxX: proto.Int32(r.X + r.Width - 1), MaxZ: proto.Int32(r.Z + r.Height - 1)})
		}
		for _, r := range l.Runs {
			shape.Runs = append(shape.Runs, &p.OverlayRun{X: proto.Int32(r.X), Z: proto.Int32(r.Z), Length: proto.Int32(r.Length)})
		}
		shapes = append(shapes, shape)
	}
	labels := make([]*p.OverlayLabel, 0, len(overlay.Labels))
	for _, l := range overlay.Labels {
		labels = append(labels, &p.OverlayLabel{Text: proto.String(l.Text), Cell: &c.Cell{X: proto.Int32(l.Cell.X), Z: proto.Int32(l.Cell.Z)}})
	}
	return shapes, labels
}
