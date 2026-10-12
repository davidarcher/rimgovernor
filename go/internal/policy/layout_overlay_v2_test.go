package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLayoutOverlayV2DrawsZonesRoomsAndReservations(t *testing.T) {
	plan := LayoutPlan{
		Rooms: []PlannedRoom{
			{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 12, Z: 23, Width: 5, Height: 5}), Door: domain.Cell{X: 14, Z: 22}, DoorRot: domain.South},
			{Role: PlannedDining, Region: RectRegion(Rectangle{X: 20, Z: 23, Width: 9, Height: 7}), Door: domain.Cell{X: 24, Z: 22}, DoorRot: domain.South},
		},
		Zones: []LayoutZone{{Kind: ZoneField, Runs: []RowRun{{Z: 0, X: 0, Length: 50}, {Z: 1, X: 240, Length: 30}}}},
		Sites: []PlannedSite{
			testSite(NodeKillbox, Rectangle{X: 80, Z: 80, Width: 5, Height: 5}),
			testSite(NodeRing, Rectangle{X: 5, Z: 5, Width: 120, Height: 120}),
			testTurbine(),
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
	// A fenced room outlines its floor and wall ring as row runs.
	if l := layers["bedroom"]; l.Style != OverlayOutline || len(l.Rects) != 0 || len(l.Runs) != 7 || l.Runs[0] != (RowRun{Z: 22, X: 11, Length: 7}) {
		t.Fatalf("bedroom %+v", l)
	}
	// The perimeter and the killbox go out as one outlined rectangle.
	for label, want := range map[string]Rectangle{
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
	for _, want := range []string{"bedroom", "dining", "turbine", "killbox"} {
		if !labels[want] {
			t.Fatalf("label %q missing: %+v", want, o.Labels)
		}
	}
	if labels["perimeter"] {
		t.Fatal("perimeter labelled")
	}
}

// testTurbine is a turbine site at (50, 50) with a wind lane beside it.
func testTurbine() PlannedSite {
	s := testSite(NodeTurbine, Rectangle{X: 50, Z: 50, Width: 7, Height: 2})
	s.Lanes = []SiteLane{{ID: "wind", Region: RectRegion(Rectangle{X: 50, Z: 52, Width: 7, Height: 16}), Wind: true}}
	return s
}

func TestSplitFieldsMovesOnlyFieldLayers(t *testing.T) {
	o := LayoutOverlay{Layers: []OverlayLayer{{Label: "field"}, {Label: "hallway"}, {Label: "field"}}, Labels: []OverlayLabel{{Text: "dining"}}}
	rest, fields := SplitFields(o)
	if len(fields.Layers) != 2 || len(rest.Layers) != 1 || rest.Layers[0].Label != "hallway" || len(rest.Labels) != 1 || len(fields.Labels) != 0 {
		t.Fatalf("rest %+v fields %+v", rest, fields)
	}
}

func runCells(runs []RowRun) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, r := range runs {
		for x := r.X; x < r.X+r.Length; x++ {
			out[domain.Cell{X: x, Z: r.Z}] = true
		}
	}
	return out
}

func TestLayoutOverlayV2DrawsShapedRoomsAsTheirCellsWithIdsAndSuccessors(t *testing.T) {
	lShape := RegionOfRuns([]RowRun{{Z: 20, X: 10, Length: 3}, {Z: 21, X: 10, Length: 3}, {Z: 22, X: 10, Length: 8}, {Z: 23, X: 10, Length: 8}})
	var ring []RowRun
	for z := int32(40); z < 47; z++ {
		if z >= 43 && z <= 44 {
			ring = append(ring, RowRun{Z: z, X: 30, Length: 2}, RowRun{Z: z, X: 35, Length: 2})
		} else {
			ring = append(ring, RowRun{Z: z, X: 30, Length: 7})
		}
	}
	ringShape := RegionOfRuns(ring)
	plan := LayoutPlan{Rooms: []PlannedRoom{
		{ID: 1, Role: PlannedKitchen, Region: lShape},
		{ID: 2, Role: PlannedDining, Region: ringShape},
		{ID: 3, Replaces: 1, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 60, Z: 20, Width: 6, Height: 6})},
	}}
	o := plan.Overlay(Bounds{Width: 100, Height: 100})
	var kitchen, dining, successor *OverlayLayer
	for i, l := range o.Layers {
		switch l.Label {
		case "kitchen":
			if kitchen == nil {
				kitchen = &o.Layers[i]
			}
		case "dining":
			dining = &o.Layers[i]
		case "successor":
			successor = &o.Layers[i]
		}
	}
	if kitchen == nil || dining == nil || successor == nil {
		t.Fatalf("layers %+v", o.Layers)
	}
	drawn := runCells(kitchen.Runs)
	for _, c := range lShape.Cells() {
		if !drawn[c] {
			t.Fatalf("L floor cell %v not drawn", c)
		}
	}
	// The notch beside the L's short arm is outside its wall ring.
	if drawn[domain.Cell{X: 17, Z: 19}] || drawn[domain.Cell{X: 16, Z: 17}] {
		t.Fatal("L outline covers its bounding box")
	}
	ringDrawn := runCells(dining.Runs)
	if len(dining.Rects) != 0 || !ringDrawn[domain.Cell{X: 30, Z: 40}] {
		t.Fatalf("ring floor: %+v", dining)
	}
	// Successors fill their floor in their role's hue.
	if successor.Style != OverlayFill || len(runCells(successor.Runs)) != 36 {
		t.Fatalf("successor %+v", successor)
	}
	labels := map[string]domain.Cell{}
	for _, l := range o.Labels {
		labels[l.Text] = l.Cell
	}
	for _, want := range []string{"#1 kitchen", "#2 dining", "#3 kitchen (replaces #1)"} {
		if _, ok := labels[want]; !ok {
			t.Fatalf("label %q missing: %+v", want, o.Labels)
		}
	}
	if !ringShape.Contains(labels["#2 dining"]) {
		t.Fatalf("ring label %v sits in the hole", labels["#2 dining"])
	}
}

func TestRequirementOverlayDrawsRequiredEdgesBetweenGroupRooms(t *testing.T) {
	plan := LayoutPlan{Rooms: []PlannedRoom{
		{ID: 2, Role: PlannedKitchen, Region: RegionOfRuns([]RowRun{{Z: 5, X: 3, Length: 3}})},
		{ID: 3, Role: PlannedFreezer, Region: RegionOfRuns([]RowRun{{Z: 15, X: 3, Length: 3}})},
	}}
	advice := StationAdvice{
		Graph: StationGraph{Edges: []StationEdge{{A: "stove", B: "cooler", Required: true}, {A: "stove", B: "cooler"}}},
		Groups: []GroupAdvice{
			{Group: RoomGroup{Stations: []GroupStation{{Node: "stove"}}}, Rooms: []int32{2}},
			{Group: RoomGroup{Stations: []GroupStation{{Node: "cooler"}}}, Rooms: []int32{3}},
		},
	}
	var o LayoutOverlay
	plan.RequirementOverlay(advice, Bounds{Width: 50, Height: 50}, &o)
	if len(o.Layers) != 1 || len(o.Layers[0].Lines) != 1 || o.Layers[0].Lines[0].From.Z != 5 || o.Layers[0].Lines[0].To.Z != 15 {
		t.Fatalf("%v", o.Layers)
	}
}
