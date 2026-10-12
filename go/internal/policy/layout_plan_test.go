package policy

import (
	"encoding/json"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestInFlightRoomsKeysByInteriorOrigin(t *testing.T) {
	a := PlannedRoom{ID: 1, Role: PlannedShelter, Region: RectRegion(Rectangle{X: 4, Z: 5, Width: 5, Height: 4})}
	b := PlannedRoom{ID: 2, Role: PlannedLab, Region: RectRegion(Rectangle{X: 20, Z: 5, Width: 5, Height: 4})}
	got := InFlightRooms(LayoutPlan{Rooms: []PlannedRoom{a, b}}, map[domain.Cell]bool{{X: 4, Z: 5}: true})
	if len(got) != 1 || !got[a.ID] {
		t.Fatal(got)
	}
}

func TestLayoutPlanAnchorFallsBackToReserve(t *testing.T) {
	p := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 0, Z: 0, Width: 4, Height: 4})},
		{Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 10, Z: 0, Width: 6, Height: 4})},
		{Role: PlannedReserve, Region: RectRegion(Rectangle{X: 20, Z: 0, Width: 2, Height: 2})},
	}}
	if c, ok := p.Anchor(PlannedKitchen, nil); !ok || c != (domain.Cell{X: 2, Z: 2}) {
		t.Fatal(c, ok)
	}
	if c, ok := p.Anchor(PlannedKitchen, func(r Rectangle) bool { return r.X != 0 }); !ok || c != (domain.Cell{X: 13, Z: 2}) {
		t.Fatal(c, ok)
	}
	if c, ok := p.Anchor(PlannedKitchen, func(r Rectangle) bool { return r.X == 20 }); !ok || c != (domain.Cell{X: 21, Z: 1}) {
		t.Fatal(c, ok)
	}
	if _, ok := p.Anchor(PlannedKitchen, func(Rectangle) bool { return false }); ok {
		t.Fatal("anchored with no free room")
	}
}

// A saved plan loads and saves with the same field names and role strings.
func TestPlannedRoomJSONRoundTripsSavedPlan(t *testing.T) {
	const saved = `{"ID":4,"Role":"kitchen","Region":{"Runs":[{"Z":2,"X":1,"Length":4},{"Z":3,"X":1,"Length":4},{"Z":4,"X":1,"Length":4}]},"Door":{"X":5,"Z":3},"DoorRot":"east","Link":null,"Dug":true}`
	var r PlannedRoom
	if err := json.Unmarshal([]byte(saved), &r); err != nil {
		t.Fatal(err)
	}
	if r.Role != PlannedKitchen || !r.Dug {
		t.Fatalf("decoded %+v", r)
	}
	out, err := json.Marshal(r)
	if err != nil {
		t.Fatal(err)
	}
	var want, got map[string]any
	if err := json.Unmarshal([]byte(saved), &want); err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(out, &got); err != nil {
		t.Fatal(err)
	}
	if len(got) != len(want) {
		t.Fatalf("saved %s, resaved %s", saved, out)
	}
	for k, v := range want {
		if g, ok := got[k]; !ok || fmt.Sprint(g) != fmt.Sprint(v) {
			t.Fatalf("field %s: saved %v, resaved %v", k, v, g)
		}
	}
}
