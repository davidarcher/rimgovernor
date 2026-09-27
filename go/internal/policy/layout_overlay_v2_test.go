package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLayoutOverlayV2DrawsZonesSpineRoomsReservationsAndTraffic(t *testing.T) {
	plan := LayoutPlan{
		Spine: []SpineSegment{{From: domain.Cell{X: 10, Z: 20}, To: domain.Cell{X: 40, Z: 20}}},
		Rooms: []LayoutRoom{
			{Role: ModuleBedroom, Interior: Rectangle{X: 12, Z: 23, Width: 5, Height: 5}, Door: domain.Cell{X: 14, Z: 22}, DoorRot: domain.South},
			{Role: ModuleDining, Interior: Rectangle{X: 20, Z: 23, Width: 9, Height: 7}, Door: domain.Cell{X: 24, Z: 22}, DoorRot: domain.South},
		},
		Zones: []LayoutZone{{Kind: ZoneField, Runs: []RowRun{{Z: 0, X: 0, Length: 50}, {Z: 1, X: 240, Length: 30}}}},
		Reservations: []LayoutReservation{
			{Kind: ReserveTurbineLane, Area: Rectangle{X: 50, Z: 50, Width: 7, Height: 18}},
			{Kind: ReserveKillbox, Area: Rectangle{X: 80, Z: 80, Width: 5, Height: 5}},
			{Kind: ReservePerimeter, Area: Rectangle{X: 5, Z: 5, Width: 120, Height: 120}},
		},
	}
	o := plan.Overlay(Bounds{Width: 250, Height: 250})
	layers := map[string]OverlayLayer{}
	for _, l := range o.Layers {
		layers[l.Label] = l
	}
	// Zones go out as filled runs, clipped to the map.
	if f := layers["field"]; f.Style != OverlayFill || len(f.Rects) != 0 || len(f.Runs) != 2 || f.Runs[1] != (RowRun{Z: 1, X: 240, Length: 10}) {
		t.Fatalf("field %+v", f)
	}
	if h := layers["hallway"]; len(h.Rects) != 1 || h.Rects[0] != (Rectangle{X: 10, Z: 19, Width: 31, Height: 3}) {
		t.Fatalf("hallway %+v", h.Rects)
	}
	// Rooms, the perimeter and the killbox go out as one outlined rectangle.
	for label, want := range map[string]Rectangle{
		"bedroom":   {X: 11, Z: 22, Width: 7, Height: 7},
		"perimeter": {X: 5, Z: 5, Width: 120, Height: 120},
		"killbox":   {X: 80, Z: 80, Width: 5, Height: 5},
	} {
		if l := layers[label]; l.Style != OverlayOutline || len(l.Runs) != 0 || len(l.Rects) != 1 || l.Rects[0] != want {
			t.Fatalf("%s %+v", label, l)
		}
	}
	if layers["turbine lane"].Style != OverlayFill {
		t.Fatalf("turbine lane %+v", layers["turbine lane"])
	}
	labels := map[string]bool{}
	for _, l := range o.Labels {
		labels[l.Text] = true
	}
	for _, want := range []string{"bedroom", "dining", "turbine lane", "killbox"} {
		if !labels[want] {
			t.Fatalf("label %q missing: %+v", want, o.Labels)
		}
	}
	if labels["perimeter"] {
		t.Fatal("perimeter labelled")
	}
	if len(layers["traffic"].Runs) == 0 {
		t.Fatal("no traffic layer")
	}
	for _, r := range layers["traffic"].Runs {
		if r.Z < 19 || r.Z > 21 {
			t.Fatalf("traffic off the spine %+v", r)
		}
	}
}
