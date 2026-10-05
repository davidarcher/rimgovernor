package policy

import (
	"encoding/json"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLayoutPlanAnchorFallsBackToReserve(t *testing.T) {
	p := LayoutPlan{Rooms: []PlannedRoom{
		{Role: PlannedKitchen, Interior: Rectangle{X: 0, Z: 0, Width: 4, Height: 4}},
		{Role: PlannedKitchen, Interior: Rectangle{X: 10, Z: 0, Width: 6, Height: 4}},
		{Role: PlannedReserve, Interior: Rectangle{X: 20, Z: 0, Width: 2, Height: 2}},
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

// A plan saved before the PlannedRoom rename (#2102) loads and saves with the
// same field names and role strings: the rename is type-level only.
func TestPlannedRoomJSONRoundTripsSavedPlan(t *testing.T) {
	const saved = `{"Role":"kitchen","Interior":{"X":1,"Z":2,"Width":4,"Height":3},"Door":{"X":5,"Z":3},"DoorRot":"east","Link":null,"Dug":true}`
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
