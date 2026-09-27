package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
)

func TestOverlayRequestSendsInclusiveRectsAndRuns(t *testing.T) {
	shapes, labels := overlayRequest(policy.LayoutOverlay{
		Layers: []policy.OverlayLayer{
			{Color: policy.OverlayColor{R: 1, A: 0.3}, Style: policy.OverlayFill, Runs: []policy.RowRun{{Z: 4, X: 3, Length: 5}}},
			{Color: policy.OverlayColor{B: 1, A: 0.85}, Style: policy.OverlayOutline, Rects: []policy.Rectangle{{X: 3, Z: 4, Width: 5, Height: 2}}},
		},
		Labels: []policy.OverlayLabel{{Text: "plaza", Cell: domain.Cell{X: 9, Z: 10}}},
	})
	run := shapes[0].Runs[0]
	if shapes[0].GetStyle() != p.OverlayStyle_OVERLAY_STYLE_FILL || run.GetX() != 3 || run.GetZ() != 4 || run.GetLength() != 5 || shapes[0].Color.GetR() != 1 || shapes[0].Color.GetA() != 0.3 {
		t.Fatalf("%v", shapes[0])
	}
	r := shapes[1].Rects[0]
	if shapes[1].GetStyle() != p.OverlayStyle_OVERLAY_STYLE_OUTLINE || r.GetMinX() != 3 || r.GetMaxX() != 7 || r.GetMinZ() != 4 || r.GetMaxZ() != 5 {
		t.Fatalf("%v", shapes[1])
	}
	if labels[0].GetText() != "plaza" || labels[0].Cell.GetX() != 9 {
		t.Fatalf("%v", labels)
	}
}
