package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func setRoof(s *excavationStep, roof string, cells ...domain.Cell) {
	for i, c := range s.facts.Cells {
		for _, r := range cells {
			if c.Cell == r {
				s.facts.Cells[i].Occupied, s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof, s.facts.Cells[i].Roofed, s.facts.Cells[i].NaturalRock = domain.Known(true), domain.Known(false), domain.Known(roof), domain.Known(roof != ""), domain.Known(true)
			}
		}
	}
}

// Thin-roof rock under an open-sky building is dug, the roof removed once
// the digging is done, and only then the building placed, on exactly its
// footprint (#1758).
func TestAdmitRockStepDigsThenUnroofsThenBuilds(t *testing.T) {
	t.Parallel()
	p, db, n, s, site, shaft := rockCoolerStep(t)
	ctx := context.Background()
	check := func() error { return nil }
	cold := policy.RefrigerationCooler{Position: site.Cell, Rotation: site.Rotation}.Cold()
	building, err := domain.NewBuilding("Cooler", site.Cell, site.Rotation, "")
	if err != nil {
		t.Fatal(err)
	}
	planned := []policy.RoleCell{{Cell: site.Cell, Role: policy.RockNeedsSky}, {Cell: shaft, Role: policy.RockNeedsSky}}

	// A thick roof or a footprint the native preview does not match refuses.
	setRoof(&s, "RoofRockThick", shaft)
	setRoof(&s, "RoofRockThin", site.Cell)
	result, handled, err := p.admitRockStep(ctx, ctx, s, planned, cold, "plan-dig-sky-thick", []domain.Building{building}, check)
	if err != nil || !handled || !result.Verdict.Is(RefusalRockNotDug) {
		t.Fatal("thick roof not refused", result, handled, err)
	}
	setRoof(&s, "RoofRockThin", shaft)
	sky := *p
	sky.exactFootprint = []domain.Cell{site.Cell, shaft}
	result, handled, err = sky.admitRockStep(ctx, ctx, s, planned, cold, "plan-dig-sky-shape", []domain.Building{building}, check)
	if err != nil || !handled || !result.Verdict.Is(RefusalNoSpace) {
		t.Fatal("footprint mismatch not refused", result, handled, err)
	}

	result, handled, err = p.admitRockStep(ctx, ctx, s, planned, cold, "plan-dig-sky", []domain.Building{building}, check)
	if err != nil || !handled || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, handled, err)
	}
	if n.overRock == 0 {
		t.Fatal("not previewed over rock")
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
		}
		if _, ok := a.Excavation(); ok {
			dug = append(dug, a.ID())
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
	if !waits[plan.Spec.Actions()[0].ID()][roof.ID()] {
		t.Fatal("the building does not wait on the roof")
	}
}
