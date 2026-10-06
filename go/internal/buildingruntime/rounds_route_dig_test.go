package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A route breach on natural rock, listed or fogged, mines the cell and
// builds the door in one method previewed over rock; an open breach needs no
// dig.
func TestDigBreachMinesRockWallThenBuildsDoor(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	for _, fogged := range []bool{false, true} {
		p, db, n, s, site, _ := rockCoolerStep(t)
		wall := site.Cell // (1,3)
		if fogged {
			var kept []policy.SiteCell
			for _, c := range s.facts.Cells {
				if c.Cell != wall {
					kept = append(kept, c)
				}
			}
			s.facts.Cells = kept
		} else {
			for i, c := range s.facts.Cells {
				if c.Cell == wall {
					s.facts.Cells[i].SetNaturalRock(true)
					s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof = domain.Known(false), domain.Known("RoofRockThick")
				}
			}
		}
		open := domain.Cell{X: 4, Z: 4}
		p.routes = &policy.RoutesProposal{Method: policy.RoutesBuild, Definition: "Door", Facility: "f", Breaches: []domain.Cell{open, wall}}
		ctx := context.Background()
		check := func() error { return nil }
		result, handled, err := p.digBreach(ctx, ctx, s, nil, check)
		if err != nil || !handled || result.Verdict != BuildingReasonAdmitted {
			t.Fatal(fogged, result, handled, err)
		}
		if n.overRock != 1 || n.overCells[0] != wall {
			t.Fatal("door previewed over rock", n.overRock, n.overCells)
		}
		plan, err := db.LoadPlan(ctx, methodPlan(t, result.Decision, "plan-dig-route-1-3"))
		if err != nil {
			t.Fatal(err)
		}
		var doors, digs int
		for _, a := range plan.Spec.Actions() {
			if b, ok := a.Building(); ok && b.Definition() == "Door" && b.Cell() == wall {
				doors++
			}
			if e, ok := a.Excavation(); ok && e.Cell() == wall {
				digs++
			}
		}
		if doors != 1 || digs != 1 {
			t.Fatal(fogged, doors, digs)
		}
	}
	p, _, _, s, _, _ := rockCoolerStep(t)
	p.routes = &policy.RoutesProposal{Method: policy.RoutesBuild, Definition: "Door", Breaches: []domain.Cell{{X: 4, Z: 4}}}
	ctx := context.Background()
	if _, handled, err := p.digBreach(ctx, ctx, s, nil, func() error { return nil }); err != nil || handled {
		t.Fatal("open breach needs no dig", handled, err)
	}
}
