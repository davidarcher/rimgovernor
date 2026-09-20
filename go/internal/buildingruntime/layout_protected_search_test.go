package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// layoutTestGrid places the sleeping fixture's 5x5 room so that only the
// four cells with x < 2 and z < 2 lie off an aisle: the grid's origin sits
// 13 cells south-west of (2,2), putting offsets 13..15 on x 2..4 and z 2..4.
func layoutTestGrid() policy.ColonyGrid {
	return policy.ColonyGrid{Origin: domain.Cell{X: 2 - 13, Z: 2 - 13}, Pitch: policy.GridPitch}
}

// The general placement search (furniture, workshop benches, generators)
// receives the grid's aisles: at Masonry the nearest free cells to the
// centre, all aisle cells, are never selected; at Camp they are.
func TestPlacementSearchNeverSelectsAnAisleAtMasonry(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	planner, _, session, _, n := sleepingFixture(t)
	identity, _, err := n.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		t.Fatal(err)
	}
	reading, err := planner.reviewer.observeColony(ctx, planner.native, expected, []string{"SleepingSpot"})
	if err != nil {
		t.Fatal(err)
	}
	grid := layoutTestGrid()
	aisles := cellSet(grid.Aisles(reading.Projection.Bounds))
	if !aisles[reading.Projection.Center] || aisles[domain.Cell{X: 1, Z: 0}] {
		t.Fatalf("test grid does not cover the centre %v", reading.Projection.Center)
	}
	snapshot := session.State().Snapshot
	snapshot.Plan, snapshot.Revision = "layout-test", 1
	check := func() error { return nil }
	for _, tier := range []policy.BuildTier{policy.BuildTierCamp, policy.BuildTierMasonry} {
		facts := reading.Projection
		facts.BuildTier, facts.ColonyGrid = domain.Known(tier), domain.Known(grid)
		selected, _, reason, err := planner.previewSearch(ctx, snapshot, facts, nil, 1, check)
		if err != nil || reason != "" || len(selected) == 0 {
			t.Fatalf("%s: %v %q %v", tier, selected, reason, err)
		}
		onAisle := false
		for _, p := range selected {
			footprint, _ := p.Footprint.Value()
			for _, c := range footprint {
				onAisle = onAisle || aisles[c]
			}
		}
		if tier == policy.BuildTierCamp && !onAisle {
			t.Fatalf("Camp search avoided the aisles: %v", selected[0].Action)
		}
		if tier == policy.BuildTierMasonry && onAisle {
			t.Fatalf("Masonry search selected an aisle cell: %v", selected[0].Action)
		}
	}
}
