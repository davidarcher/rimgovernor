package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestTrafficOverlayScalesToBusiestCell(t *testing.T) {
	cells := []TrafficCell{
		{Cell: domain.Cell{X: 1, Z: 1}, Layer: TrafficColonist, Samples: 100},
		{Cell: domain.Cell{X: 2, Z: 1}, Layer: TrafficColonist, Samples: 90},
		{Cell: domain.Cell{X: 5, Z: 5}, Layer: TrafficColonist, Samples: 5},
		{Cell: domain.Cell{X: 9, Z: 9}, Layer: TrafficHostile, Samples: 500},
		{Cell: domain.Cell{X: 99, Z: 1}, Layer: TrafficColonist, Samples: 50},
	}
	got := TrafficOverlay(cells, TrafficColonist, Bounds{Width: 20, Height: 20})
	if len(got.Layers) != 2 {
		t.Fatalf("layers = %+v, want a low and a top band", got.Layers)
	}
	low, top := got.Layers[0], got.Layers[1]
	if low.Style != OverlayFill || len(low.Runs) != 1 || low.Runs[0] != (RowRun{Z: 5, X: 5, Length: 1}) {
		t.Fatalf("low band = %+v", low)
	}
	if len(top.Runs) != 1 || top.Runs[0] != (RowRun{Z: 1, X: 1, Length: 2}) || top.Color.A <= low.Color.A {
		t.Fatalf("top band = %+v, low alpha %v", top, low.Color.A)
	}
	if empty := TrafficOverlay(cells, TrafficAnimal, Bounds{Width: 20, Height: 20}); len(empty.Layers) != 0 {
		t.Fatalf("animal layer = %+v, want empty", empty)
	}
}
