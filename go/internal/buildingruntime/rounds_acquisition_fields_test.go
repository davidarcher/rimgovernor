package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A tree inside a growing zone the fields plan just created, or one the
// planning window observes, is never selected (#1361).
func TestWithoutFieldSourcesDropsZonedPlants(t *testing.T) {
	zone, err := domain.NewZoneCreate(domain.GrowingZone, "Plant_Potato", []domain.Cell{{X: 119, Z: 124}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction("field-1", zone)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("plan-field", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := domain.NewProgress(spec, "field-1")
	if err != nil {
		t.Fatal(err)
	}
	projection := observation.ColonyProjection{
		Farms: []observation.FarmZoneFact{{ID: "Zone_1", Crop: "Plant_Rice"}},
		Cells: []policy.SiteCell{{Cell: domain.Cell{X: 5, Z: 5}, ZoneID: domain.Known("Zone_1")}, {Cell: domain.Cell{X: 6, Z: 6}, ZoneID: domain.Known("Zone_2")}},
	}
	rows := []policy.AcquisitionSource{
		{ID: "TreeInNewField", Cell: domain.Cell{X: 119, Z: 124}, Tree: true},
		{ID: "TreeInFarm", Cell: domain.Cell{X: 5, Z: 5}, Tree: true},
		{ID: "TreeInStockpile", Cell: domain.Cell{X: 6, Z: 6}, Tree: true},
		{ID: "HuntInFarm", Cell: domain.Cell{X: 5, Z: 5}, Hunt: true},
	}
	current := domain.GenerationSnapshot{Colony: "c", Load: "l", Native: 1}
	got, known := withoutFieldSources(domain.Known(rows), projection, []store.PlanState{{Spec: spec, Progress: []domain.Progress{progress}}}, current).Value()
	if !known || len(got) != 2 || got[0].ID != "TreeInStockpile" || got[1].ID != "HuntInFarm" {
		t.Fatalf("kept %+v", got)
	}
}
