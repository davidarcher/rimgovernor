package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A planner's fresh read of the load the review fixed a grid on plans on
// that grid (#667: layout/grid sited unaligned fields when Fields missed
// the census); another load does not inherit it.
func TestRoutineCensusServesReviewedGrid(t *testing.T) {
	var s routineCensusStore
	review := observation.Identity{Colony: "c", Map: 0, Load: "l", Tick: 15}
	grid := policy.ColonyGrid{Origin: domain.Cell{X: 117, Z: 133}, Pitch: policy.GridPitch}
	s.rememberGrid(review, domain.Known(grid))

	fresh := observation.ColonyProjection{Identity: observation.Identity{Colony: "c", Map: 0, Load: "l", Tick: 16}, ColonyGrid: domain.Unknown[policy.ColonyGrid]()}
	s.serveGrid(&fresh)
	if got, ok := fresh.ColonyGrid.Value(); !ok || got != grid {
		t.Fatalf("fresh read of the reviewed load got grid %+v known=%t, want %+v", got, ok, grid)
	}
	other := observation.ColonyProjection{Identity: observation.Identity{Colony: "c", Map: 0, Load: "other"}, ColonyGrid: domain.Unknown[policy.ColonyGrid]()}
	s.serveGrid(&other)
	if _, ok := other.ColonyGrid.Value(); ok {
		t.Fatal("another load inherited the reviewed grid")
	}
}
