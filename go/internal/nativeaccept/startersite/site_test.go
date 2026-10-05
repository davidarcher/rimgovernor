package startersite

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestPlannedSiteStandsOnThePlannedShelter(t *testing.T) {
	bounds := policy.Bounds{Width: 50, Height: 50}
	store := policy.PlannedRoom{Role: policy.PlannedShelter, Interior: policy.Rectangle{X: 11, Z: 11, Width: 7, Height: 7}, Door: domain.Cell{X: 14, Z: 10}, DoorRot: domain.South}
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{store}}
	// The 9x9 hut is the shelter's ring: its door is kept.
	site, door, ok := plannedSite(plan, bounds, 9)
	if !ok || site != (domain.Cell{X: 10, Z: 10}) || door != store.Door {
		t.Fatal(site, door, ok)
	}
	// A 7x7 hut on the same corner keeps the south door on its ring.
	if site, door, ok = plannedSite(plan, bounds, 7); !ok || site != (domain.Cell{X: 10, Z: 10}) || door != store.Door {
		t.Fatal(site, door, ok)
	}
	// Near the map edge the square is pulled inside; a door off its ring
	// falls back to the mid east wall.
	site, door, ok = plannedSite(plan, policy.Bounds{Width: 18, Height: 18}, 11)
	if !ok || site != (domain.Cell{X: 7, Z: 7}) || door != (domain.Cell{X: 17, Z: 12}) {
		t.Fatal(site, door, ok)
	}
	if _, _, ok = plannedSite(policy.LayoutPlan{}, bounds, 9); ok {
		t.Fatal("sited without a planned shelter")
	}
}
