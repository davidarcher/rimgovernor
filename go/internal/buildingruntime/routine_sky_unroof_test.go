package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// An unroof-only plan reads no rock, so a native with no excavation source
// still admits it: the roof comes off, then the building is placed (#1872).
// Once the roof job was designated for excavationStallTicks and the roof is
// still on, roofStalled names it.
func TestUnroofOnlyPlanNeedsNoExcavationSourceAndStallIsNamed(t *testing.T) {
	t.Parallel()
	p, db, n, s, site, _ := rockCoolerStep(t)
	ctx := context.Background()
	for i, c := range s.facts.Cells {
		if c.Cell == site.Cell {
			s.facts.Cells[i].Occupied, s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof, s.facts.Cells[i].NaturalRock = domain.Known(false), domain.Known(true), domain.Known("RoofConstructed"), domain.Known(false)
		}
	}
	building, err := domain.NewBuilding("Cooler", site.Cell, site.Rotation, "")
	if err != nil {
		t.Fatal(err)
	}
	cold := policy.RefrigerationCooler{Position: site.Cell, Rotation: site.Rotation}.Cold()
	p.native = noExcavationNative{n.refrigerationNative, n}
	method := skyMethod("Cooler", policy.Rectangle{X: 1, Z: 3})
	planned := []policy.RoleCell{{Cell: site.Cell, Role: policy.RockNeedsSky}}
	result, handled, err := p.admitRockStep(ctx, ctx, s, planned, cold, method, []domain.Building{building}, func() error { return nil })
	if err != nil || !handled || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, handled, err)
	}
	plan, err := db.LoadPlan(ctx, methodPlan(t, result.Decision, method))
	if err != nil {
		t.Fatal(err)
	}
	roofs := 0
	for _, a := range plan.Spec.Actions() {
		if _, ok := a.RemoveRoof(); ok {
			roofs++
		}
	}
	if roofs != 1 {
		t.Fatal("no remove_roof action", plan.Spec.Actions())
	}
	if stalled, err := p.roofStalled(ctx, s, method); err != nil || stalled {
		t.Fatal("stalled before the plan ran", stalled, err)
	}
	snapshot := result.Decision.Goal.Standard.Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for _, a := range plan.Spec.Actions() {
		if _, ok := a.RemoveRoof(); !ok {
			continue
		}
		if _, err := db.Prepare(ctx, plan.Spec.ID(), a.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err := db.Dispatch(ctx, plan.Spec.ID(), a.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err := db.RecordReceipt(ctx, plan.Spec.ID(), a.ID(), 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
	}
	s.facts.Identity.Tick = 7 + excavationStallTicks - 1
	if stalled, err := p.roofStalled(ctx, s, method); err != nil || stalled {
		t.Fatal("stalled before the bound", stalled, err)
	}
	s.facts.Identity.Tick = 7 + excavationStallTicks
	if stalled, err := p.roofStalled(ctx, s, method); err != nil || !stalled {
		t.Fatal("stall not named", stalled, err)
	}
}

// noExcavationNative is the refrigeration native without an excavation
// source, still serving the roof rows.
type noExcavationNative struct {
	*refrigerationNative
	roofs *rockCoolerNative
}

func (n noExcavationNative) DefinitionCatalog(ctx context.Context, id *c.Identity) (*bridge.DefinitionCatalog, error) {
	return n.roofs.DefinitionCatalog(ctx, id)
}
