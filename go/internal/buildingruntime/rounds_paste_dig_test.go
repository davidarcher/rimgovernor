package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// A paste site whose hopper cell is rock (listed or fogged) mines it and
// builds dispenser and hopper in one method, both previewed with the rock
// cell previewed over rock; an open site needs no dig.
func TestDigPasteMinesRockUnderHopperThenBuildsBoth(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	for _, fogged := range []bool{false, true} {
		p, db, n, s, site, _ := rockCoolerStep(t)
		hopper := site.Cell // (1,3) is rock in the fixture
		if fogged {
			var kept []policy.SiteCell
			for _, c := range s.facts.Cells {
				if c.Cell != hopper {
					kept = append(kept, c)
				}
			}
			s.facts.Cells = kept
		} else {
			for i, c := range s.facts.Cells {
				if c.Cell == hopper {
					s.facts.Cells[i].Occupied, s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof, s.facts.Cells[i].NaturalRock = domain.Known(true), domain.Known(false), domain.Known("RoofRockThick"), domain.Known(true)
				}
			}
		}
		p.paste = []policy.SiteBuilding{{Definition: "NutrientPasteDispenser", Cell: domain.Cell{X: 4, Z: 4}, Rotation: domain.North}, {Definition: "Hopper", Cell: hopper, Rotation: domain.North}}
		ctx := context.Background()
		result, handled, err := p.digPaste(ctx, ctx, s, nil, func() error { return nil })
		if err != nil || !handled || result.Verdict != BuildingReasonAdmitted {
			t.Fatal(fogged, result, handled, err)
		}
		if n.overRock != 2 || n.overCells[1] != hopper {
			t.Fatal("both buildings preview over rock", n.overRock, n.overCells)
		}
		plan, err := db.LoadPlan(ctx, methodPlan(t, result.Decision, "plan-dig-paste-1-3"))
		if err != nil {
			t.Fatal(err)
		}
		var built, digs int
		for _, a := range plan.Spec.Actions() {
			if _, ok := a.Building(); ok {
				built++
			}
			if e, ok := a.Excavation(); ok && e.Cell() == hopper {
				digs++
			}
		}
		if built != 2 || digs != 1 {
			t.Fatal(fogged, built, digs)
		}
	}
	p, _, _, s, _, _ := rockCoolerStep(t)
	p.paste = []policy.SiteBuilding{{Definition: "Hopper", Cell: domain.Cell{X: 4, Z: 4}, Rotation: domain.North}}
	ctx := context.Background()
	if _, handled, err := p.digPaste(ctx, ctx, s, nil, func() error { return nil }); err != nil || handled {
		t.Fatal("open site needs no dig", handled, err)
	}
}
