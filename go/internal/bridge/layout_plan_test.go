package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestLayoutPlanRequestSendsInclusiveRects(t *testing.T) {
	layers, labels, rooms := layoutPlanRequest(policy.LayoutOverlay{
		Layers: []policy.OverlayLayer{{Color: "PlanGray", Label: "aisles", Rects: []policy.Rectangle{{X: 3, Z: 4, Width: 5, Height: 1}}}},
		Labels: []policy.OverlayLabel{{Text: "plaza", Cell: domain.Cell{X: 9, Z: 10}}},
		Rooms:  []policy.OverlayRoomColor{{Role: "Bedroom", Color: "PlanBlue", Label: "bedroom"}},
	})
	r := layers[0].Rects[0]
	if r.GetMinX() != 3 || r.GetMaxX() != 7 || r.GetMinZ() != 4 || r.GetMaxZ() != 4 || layers[0].GetColorDef() != "PlanGray" {
		t.Fatalf("%v", layers[0])
	}
	if labels[0].GetText() != "plaza" || labels[0].Cell.GetX() != 9 || rooms[0].GetRoleDef() != "Bedroom" {
		t.Fatalf("%v %v", labels, rooms)
	}
}
