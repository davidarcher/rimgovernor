package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// DrawLayoutPlan replaces the colony layout overlay (#726): the native
// deletes every plan it owns and draws the overlay's layers, room colors
// and labels; enabled=false only deletes. Output only, never read back.
func (client *Client) DrawLayoutPlan(ctx context.Context, identity *c.Identity, overlay policy.LayoutOverlay, enabled bool) (*p.LayoutPlanApplied, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &p.LayoutPlanRequest{Identity: proto.Clone(identity).(*c.Identity), Enabled: proto.Bool(enabled)}
	if enabled {
		request.Layers, request.Labels, request.RoomColors = layoutPlanRequest(overlay)
	}
	reply := &p.LayoutPlanReply{}
	raw, err := client.protoCall(ctx, "rimgovernor/presentation_layout_plan", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.LayoutPlanReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	case *p.LayoutPlanReply_Applied:
		if v.Applied.GetContext() == nil {
			return nil, raw, contract("layout plan context required")
		}
		return v.Applied, raw, nil
	}
	return nil, raw, contract("layout plan outcome required")
}

func layoutPlanRequest(overlay policy.LayoutOverlay) ([]*p.LayoutPlanLayer, []*p.LayoutPlanLabel, []*p.LayoutPlanRoomColor) {
	layers := make([]*p.LayoutPlanLayer, 0, len(overlay.Layers))
	for _, l := range overlay.Layers {
		layer := &p.LayoutPlanLayer{ColorDef: proto.String(l.Color), Label: proto.String(l.Label)}
		for _, r := range l.Rects {
			layer.Rects = append(layer.Rects, &p.MapRect{MinX: proto.Int32(r.X), MinZ: proto.Int32(r.Z), MaxX: proto.Int32(r.X + r.Width - 1), MaxZ: proto.Int32(r.Z + r.Height - 1)})
		}
		layers = append(layers, layer)
	}
	labels := make([]*p.LayoutPlanLabel, 0, len(overlay.Labels))
	for _, l := range overlay.Labels {
		labels = append(labels, &p.LayoutPlanLabel{Text: proto.String(l.Text), Cell: &c.Cell{X: proto.Int32(l.Cell.X), Z: proto.Int32(l.Cell.Z)}})
	}
	rooms := make([]*p.LayoutPlanRoomColor, 0, len(overlay.Rooms))
	for _, r := range overlay.Rooms {
		rooms = append(rooms, &p.LayoutPlanRoomColor{RoleDef: proto.String(r.Role), ColorDef: proto.String(r.Color), Label: proto.String(r.Label)})
	}
	return layers, labels, rooms
}
