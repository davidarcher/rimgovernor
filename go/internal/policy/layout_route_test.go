package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCheckRoutesPlannedCore(t *testing.T) {
	p := PlanCore(coreTestZones(), 6, BuildTierCamp)
	traffic, err := CheckRoutes(p)
	if err != nil {
		t.Fatal(err)
	}
	seg := p.Spine[0]
	busiest := 0
	for c, n := range traffic {
		if c.Z >= seg.From.Z-1 && c.Z <= seg.From.Z+1 && n > busiest {
			busiest = n
		}
	}
	// Six bedroom trips alone share the hallway.
	if busiest < 6 {
		t.Fatal("hallway traffic", busiest)
	}
}

func TestCheckRoutesRejectsThoroughfare(t *testing.T) {
	// Storage sits behind a bedroom and its only door opens into it, so the
	// entrance -> storage trip walks through the bedroom.
	p := LayoutPlan{
		Spine:     []SpineSegment{{From: domain.Cell{X: 0, Z: 0}, To: domain.Cell{X: 20, Z: 0}}},
		Entrances: spineEntrances([]SpineSegment{{From: domain.Cell{X: 0, Z: 0}, To: domain.Cell{X: 20, Z: 0}}}),
		Rooms: []LayoutRoom{
			{Role: ModuleBedroom, Interior: Rectangle{X: 0, Z: 3, Width: 5, Height: 5}, Door: domain.Cell{X: 2, Z: 2}, DoorRot: domain.South},
			{Role: ModuleStorage, Interior: Rectangle{X: 0, Z: 9, Width: 5, Height: 5}, Door: domain.Cell{X: 2, Z: 8}, DoorRot: domain.South},
		},
	}
	if _, err := CheckRoutes(p); err == nil || !strings.Contains(err.Error(), "crosses bedroom") {
		t.Fatal("thoroughfare passed", err)
	}
	// The same storage with its own hallway door passes.
	p.Rooms[1].Interior.X, p.Rooms[1].Interior.Z = 8, 3
	p.Rooms[1].Door = domain.Cell{X: 10, Z: 2}
	if _, err := CheckRoutes(p); err != nil {
		t.Fatal(err)
	}
}
