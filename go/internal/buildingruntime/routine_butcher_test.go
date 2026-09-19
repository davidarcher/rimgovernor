package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The butcher spot is owed on the food runway alone (#260): an unarmed
// colony short of food still gets the spot, so the bill and then the hunt
// rows follow as soon as the equip family arms someone.
func TestButcherSpotSelectionIgnoresArmedCount(t *testing.T) {
	t.Parallel()
	r := &RoutineBuildingPlanner{reviewer: &RoutineReviewer{policy: policy.DefaultRoutinePolicy()}, goal: policy.EnsureFoodSupply, definition: "ButcherSpot"}
	f := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(8)), FoodDays: domain.Known(1.75), Armed: domain.Known(int64(0))}}
	f.ButcheringBenches = domain.Known([]observation.CookingBench{})
	if n, id, reason := r.selection(f); n != 1 || id != "butcher-spot" || reason != "" {
		t.Fatal(n, id, reason)
	}
	f.Facts.Armed = domain.Unknown[int64]()
	if n, id, reason := r.selection(f); n != 1 || id != "butcher-spot" || reason != "" {
		t.Fatal(n, id, reason)
	}
	f.Facts.FoodDays = domain.Known(7.0)
	if _, _, reason := r.selection(f); reason != BuildingMethodNoDeficit {
		t.Fatal(reason)
	}
}

// An open butcher-spot build under EnsureFoodSupply does not block the next
// field batch; open zone work still does.
func TestFieldBlockingWorkIgnoresButcherSpot(t *testing.T) {
	t.Parallel()
	spot, err := domain.NewBuilding("ButcherSpot", domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	build, err := domain.NewBuildingAction("a-spot", spot)
	if err != nil {
		t.Fatal(err)
	}
	zone, err := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", []domain.Cell{{X: 2, Z: 2}})
	if err != nil {
		t.Fatal(err)
	}
	sow, err := domain.NewZoneCreateAction("a-zone", zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("p", 1, []domain.Action{build, sow})
	if err != nil {
		t.Fatal(err)
	}
	spotOpen, err := domain.NewProgress(plan, "a-spot")
	if err != nil {
		t.Fatal(err)
	}
	if fieldBlockingWork([]domain.Progress{spotOpen}) {
		t.Fatal("butcher spot blocked the field planner")
	}
	zoneOpen, err := domain.NewProgress(plan, "a-zone")
	if err != nil {
		t.Fatal(err)
	}
	if !fieldBlockingWork([]domain.Progress{spotOpen, zoneOpen}) {
		t.Fatal("open zone work did not block the field planner")
	}
}

// Open forage work leaves the hunt rows plannable (#260); an open hunt or
// any other open action keeps the one-plan rule.
func TestAcquisitionHuntOnlyOpen(t *testing.T) {
	t.Parallel()
	berry, err := domain.NewAcquisition("bush", "RawBerries", domain.Cell{X: 1, Z: 1})
	if err != nil {
		t.Fatal(err)
	}
	hare, err := domain.NewAcquisition("hare", "Corpse_Hare", domain.Cell{X: 2, Z: 2})
	if err != nil {
		t.Fatal(err)
	}
	forage, err := domain.NewAcquisitionAction("a-bush", berry)
	if err != nil {
		t.Fatal(err)
	}
	hunt, err := domain.NewAcquisitionAction("a-hare", hare)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("p", 1, []domain.Action{forage, hunt})
	if err != nil {
		t.Fatal(err)
	}
	forageOpen, err := domain.NewProgress(plan, "a-bush")
	if err != nil {
		t.Fatal(err)
	}
	huntOpen, err := domain.NewProgress(plan, "a-hare")
	if err != nil {
		t.Fatal(err)
	}
	hunts := map[string]bool{"hare": true}
	if !acquisitionHuntOnlyOpen([]domain.Progress{forageOpen}, hunts) {
		t.Fatal("open forage did not leave hunts plannable")
	}
	if acquisitionHuntOnlyOpen([]domain.Progress{forageOpen, huntOpen}, hunts) {
		t.Fatal("an open hunt left hunts plannable")
	}
	if acquisitionHuntOnlyOpen(nil, hunts) {
		t.Fatal("no open work read as open forage")
	}
	rows, _ := huntRows(domain.Known([]policy.AcquisitionSource{{ID: "bush"}, {ID: "hare", Hunt: true}})).Value()
	if len(rows) != 1 || rows[0].ID != "hare" {
		t.Fatal(rows)
	}
}
