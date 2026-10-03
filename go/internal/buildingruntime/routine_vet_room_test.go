package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestVetRoomReadyIsUnknownWhileAFactIs(t *testing.T) {
	if _, known := vetRoomReady(observation.ColonyProjection{}).Value(); known {
		t.Fatal("an unread plan, room or construction census leaves Ready unknown")
	}
}

func TestBotAreasPlanTheVetRoomAndKeepItOutOfSafe(t *testing.T) {
	plan := policy.LayoutPlan{Reservations: []policy.LayoutReservation{{Kind: policy.ReserveVetRoom, Area: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 6}}}}
	vet := plan.VetRoomCells()
	if len(vet) != 16 {
		t.Fatal("the vet room interior is 4x4", len(vet))
	}
	// One enclosed, roofed room exactly on the vet room interior.
	room := policy.Room{ID: "1", Enclosed: domain.Known(true), Roofed: domain.Known(true), Cells: vet}
	projection := observation.ColonyProjection{
		Rooms:      domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{room}}),
		LayoutPlan: domain.Known(plan),
	}
	var m safeAreaMemory
	if owed, err := m.review("w", projection); err != nil {
		t.Fatal(err)
	} else if v, _ := owed.Value(); !v {
		t.Fatal("a new area is owed")
	}
	creates := map[string]int{}
	for _, edit := range m.take("w") {
		if edit.Operation() == domain.AreaCreate {
			creates[edit.Key()] = len(edit.Cells())
		}
	}
	if creates[policy.VetRoomAreaKey] != len(vet) {
		t.Fatal("the VetRoom area covers the vet room interior", creates)
	}
	if creates[policy.SafeAreaKey] != 0 {
		t.Fatal("the Safe area leaves the vet room out", creates)
	}
}
