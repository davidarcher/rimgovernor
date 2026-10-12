package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCheckRoutesRejectsThoroughfare(t *testing.T) {
	// Storage sits behind a bedroom and its only door opens into it, so the
	// entrance -> storage trip walks through the bedroom.
	p := LayoutPlan{
		Spine:     []SpineSegment{{From: domain.Cell{X: 0, Z: 0}, To: domain.Cell{X: 20, Z: 0}}},
		Entrances: spineEntrances([]SpineSegment{{From: domain.Cell{X: 0, Z: 0}, To: domain.Cell{X: 20, Z: 0}}}),
		Rooms: []PlannedRoom{
			{Role: PlannedBedroom, Region: RectRegion(Rectangle{X: 0, Z: 3, Width: 5, Height: 5}), Door: domain.Cell{X: 2, Z: 2}, DoorRot: domain.South},
			{Role: PlannedStorage, Region: RectRegion(Rectangle{X: 0, Z: 9, Width: 5, Height: 5}), Door: domain.Cell{X: 2, Z: 8}, DoorRot: domain.South},
		},
	}
	if _, err := CheckRoutes(p); err == nil || !strings.Contains(err.Error(), "crosses bedroom") {
		t.Fatal("thoroughfare passed", err)
	}
	// The same storage with its own hallway door passes.
	moved := p.Rooms[1].Region.Bounds()
	moved.X, moved.Z = 8, 3
	p.Rooms[1].Region = RectRegion(moved)
	p.Rooms[1].Door = domain.Cell{X: 10, Z: 2}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal(err)
	}
}
