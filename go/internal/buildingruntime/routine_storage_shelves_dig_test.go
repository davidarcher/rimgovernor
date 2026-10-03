package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A shelf whose footprint is rock, listed or fogged, is mined and built in
// one method (previewed over rock); a footprint on open ground builds as
// before.
func TestShelfOnRockMinesFootprintThenBuildsShelf(t *testing.T) {
	t.Parallel()
	for _, fogged := range []bool{false, true} {
		p, db, n, s, site, _ := rockCoolerStep(t)
		cell := site.Cell // (1,3) is rock in the fixture
		if fogged {
			var kept []policy.SiteCell
			for _, c := range s.facts.Cells {
				if c.Cell != cell {
					kept = append(kept, c)
				}
			}
			s.facts.Cells = kept
		} else {
			for i, c := range s.facts.Cells {
				if c.Cell == cell {
					s.facts.Cells[i].Occupied, s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof = domain.Known(true), domain.Known(false), domain.Known("RoofRockThick")
				}
			}
		}
		r := &RoutineStorageShelvesPlanner{reviewer: p.reviewer, native: n}
		call, epoch, done, err := p.reviewer.player.enter(context.Background(), "test", false)
		if err != nil {
			t.Fatal(err)
		}
		defer done()
		step := policy.ShelfStep{Zone: policy.ShelfZone{Zone: "z1"}, Pieces: []policy.InteriorPiece{{Slot: "shelf", Def: policy.ShelfDefinition, Size: domain.Cell{X: 1, Z: 1}, Rot: domain.North, Rect: policy.Rectangle{X: cell.X, Z: cell.Z, Width: 1, Height: 1}}}}
		reading := observation.RoutineReading{ColonyReading: s.read}
		reading.Projection = s.facts
		result, err := r.build(call, epoch, s.state, s.review, s.goal, policy.SecureSupplies, reading, step, 0)
		if err != nil || result.Verdict != BuildingReasonAdmitted || result.Plan == "" {
			t.Fatal(fogged, result, err)
		}
		if n.overRock != 1 || n.overCells[0] != cell {
			t.Fatal(n.overRock, n.overCells)
		}
		plan, err := db.LoadPlan(context.Background(), result.Plan)
		if err != nil {
			t.Fatal(err)
		}
		var shelves, digs int
		for _, a := range plan.Spec.Actions() {
			if b, ok := a.Building(); ok && b.Cell() == cell {
				shelves++
			}
			if e, ok := a.Excavation(); ok && e.Cell() == cell {
				digs++
			}
		}
		if shelves != 1 || digs != 1 {
			t.Fatal(fogged, shelves, digs)
		}
	}
}
