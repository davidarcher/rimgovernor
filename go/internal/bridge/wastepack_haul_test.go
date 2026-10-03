package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// A wastepack haul is the HAUL designation under the wastepack guard on the
// exact pack; the pollution-clear area edit carries pollution_clear and no
// home or key (#1683).
func TestWastepackHaulAndPollutionClearIntents(t *testing.T) {
	haul, err := domain.NewWastepackHaul("Wastepack12", "Wastepack", domain.Cell{X: 7, Z: 8})
	if err != nil {
		t.Fatal(err)
	}
	action, _ := domain.NewWastepackHaulAction("w", haul)
	wire, err := IntentAction("plan/1", action)
	if err != nil {
		t.Fatal(err)
	}
	d := wire.GetDesignate()
	if d.GetDesignation() != o.ThingDesignation_THING_DESIGNATION_HAUL || d.GetGuard() != o.DesignationGuard_DESIGNATION_GUARD_WASTEPACK ||
		d.GetTarget().GetId() != "Wastepack12" || d.GetExpectedDef() != "Wastepack" || d.GetCell().GetX() != 7 {
		t.Fatalf("%v", wire)
	}
	area, _ := domain.NewPollutionClearArea(domain.AreaClearCells, []domain.Cell{{X: 1, Z: 1}})
	areaAction, _ := domain.NewAreaAction("a", area)
	wire, err = IntentAction("plan/1", areaAction)
	if err != nil {
		t.Fatal(err)
	}
	a := wire.GetArea()
	if !a.GetPollutionClear() || a.Home != nil || a.Key != nil || a.GetOperation() != o.AreaOperation_AREA_OPERATION_CLEAR_CELLS {
		t.Fatalf("%v", wire)
	}
	if _, err = domain.NewPollutionClearArea(domain.AreaCreate, nil); err == nil {
		t.Fatal("the pollution-clear area is never created")
	}
}
