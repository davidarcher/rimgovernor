package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func setRoof(s *excavationStep, roof string, cells ...domain.Cell) {
	for i, c := range s.facts.Cells {
		for _, r := range cells {
			if c.Cell == r {
				s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof, s.facts.Cells[i].Roofed = domain.Known(false), domain.Known(roof), domain.Known(roof != "")
				s.facts.Cells[i].SetNaturalRock(true)
			}
		}
	}
}

// Thin-roof rock under an open-sky site is dug and the roof removed once the
// digging is done; the plan holds no building, which the ordinary preview
// places when the site reads clear.
func TestAdmitRockStepDigsThenUnroofsWithoutBuilding(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	p, db, n, s, site, shaft := rockCoolerStep(t)
	ctx := context.Background()
	check := func() error { return nil }
	cold := policy.RefrigerationCooler{Position: site.Cell, Rotation: site.Rotation}.Cold()
	planned := []policy.RoleCell{{Cell: site.Cell, Role: policy.RockNeedsSky}, {Cell: shaft, Role: policy.RockNeedsSky}}

	// A thick roof refuses.
	setRoof(&s, "RoofRockThick", shaft)
	setRoof(&s, "RoofRockThin", site.Cell)
	result, handled, err := p.admitRockStep(ctx, ctx, s, planned, cold, "plan-dig-sky-thick", nil, check)
	if err != nil || !handled || !result.Verdict.Is(policy.CauseRockNotDug) {
		t.Fatal("thick roof not refused", result, handled, err)
	}

	setRoof(&s, "RoofRockThin", shaft)
	result, handled, err = p.admitRockStep(ctx, ctx, s, planned, cold, "plan-dig-sky", nil, check)
	if err != nil || !handled || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, handled, err)
	}
	if n.overRock != 0 {
		t.Fatal("a building was previewed", n.overRock)
	}
	plan, err := db.LoadPlan(ctx, methodPlan(t, result.Decision, "plan-dig-sky"))
	if err != nil {
		t.Fatal(err)
	}
	var roof domain.Action
	var dug []domain.ActionID
	for _, a := range plan.Spec.Actions() {
		if _, ok := a.RemoveRoof(); ok {
			roof = a
		} else if _, ok := a.Excavation(); ok {
			dug = append(dug, a.ID())
		} else {
			t.Fatal("unexpected action", a)
		}
	}
	if len(dug) != 2 || roof.ID() == "" {
		t.Fatal("actions", plan.Spec.Actions())
	}
	cells, _ := roof.RemoveRoof()
	if len(cells.Cells()) != 2 {
		t.Fatal(cells.Cells())
	}
	waits := map[domain.ActionID]map[domain.ActionID]bool{}
	for _, d := range plan.Spec.Dependencies() {
		if waits[d.Action] == nil {
			waits[d.Action] = map[domain.ActionID]bool{}
		}
		waits[d.Action][d.Requires] = true
	}
	for _, id := range dug {
		if !waits[roof.ID()][id] {
			t.Fatal("the roof does not wait on the dig", id)
		}
	}
}
