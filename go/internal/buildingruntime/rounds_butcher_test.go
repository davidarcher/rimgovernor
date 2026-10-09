package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The butcher table is owed whenever the goal is, whatever the food runway,
// once the wood and a builder are there; there is no stand-in,
// so without them the goal waits.
func TestButcherTableSelectionIgnoresArmedCount(t *testing.T) {
	t.Parallel()
	r := &RoundsBuildingPlanner{reviewer: &Rounder{policy: policy.DefaultRoundsPolicy()}, concern: policy.MaintainButcherSpot, definition: "TableButcher"}
	f := observation.ColonyProjection{Facts: policy.RoundsFacts{Colonists: domain.Known(int64(8)), FoodDays: domain.Known(1.75), Armed: domain.Known(int64(0)), Wood: domain.Known(policy.ButcherTableWood)}}
	f.ButcheringBenches = domain.Known([]observation.CookingBench{})
	f.Definitions = []observation.PlanningDefinition{{Name: "TableButcher", Available: domain.Known(true), ConstructionSkill: domain.Known(int32(0))}}
	f.WorkPawns = domain.Known([]policy.WorkPawn{{Available: domain.Known(true), Applies: domain.Known(true), Work: domain.Known([]policy.WorkPriority{{Work: policy.WorkConstruction, Priority: 1}})}})
	for _, armed := range []domain.Fact[int64]{domain.Known(int64(0)), domain.Unknown[int64]()} {
		f.Facts.Armed = armed
		if n, id, reason := r.selection(f); n != 1 || id != "butcher-table" || !reason.IsZero() {
			t.Fatal(n, id, reason)
		}
	}
	f.Facts.FoodDays = domain.Known(10.0)
	if n, id, reason := r.selection(f); n != 1 || id != "butcher-table" || !reason.IsZero() {
		t.Fatal("the table waited on the food runway", n, id, reason)
	}
	f.Facts.Wood = domain.Known(policy.ButcherTableWood - 1)
	if n, _, reason := r.selection(f); n != 0 || reason != BuildingExistingFacility {
		t.Fatal("no wood still raised a table", n, reason)
	}
}

// An open butcher-table build under MaintainButcherSpot does not block the next
// field batch; open zone work still does.
func TestFieldBlockingWorkIgnoresButcherTable(t *testing.T) {
	t.Parallel()
	spot, err := domain.NewBuilding("TableButcher", domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	build, err := domain.NewBuildingAction("a-spot", spot, domain.TierExpand)
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
		t.Fatal("butcher table blocked the field planner")
	}
	zoneOpen, err := domain.NewProgress(plan, "a-zone")
	if err != nil {
		t.Fatal(err)
	}
	if !fieldBlockingWork([]domain.Progress{spotOpen, zoneOpen}) {
		t.Fatal("open zone work did not block the field planner")
	}
}

// Blocking reads designated census rows: a designated forage row
// leaves the hunt rows plannable; a designated hunt, or for wood a
// designated tree, is existing work; held (cooled) rows and undesignated
// ones never block.
func TestCensusBlocking(t *testing.T) {
	t.Parallel()
	bush := policy.AcquisitionSource{ID: "bush", Resource: "RawBerries", Food: true, NutritionYield: 1}
	hare := policy.AcquisitionSource{ID: "hare", Resource: "Meat_Hare", Food: true, NutritionYield: 1, Hunt: true}
	oak := policy.AcquisitionSource{ID: "oak", Resource: "WoodLog", Tree: true, Yield: 10}
	designated := func(rows ...policy.AcquisitionSource) domain.Fact[[]policy.AcquisitionSource] {
		for i := range rows {
			rows[i].Designated = true
		}
		return domain.Known(rows)
	}
	for _, c := range []struct {
		name          string
		rows          domain.Fact[[]policy.AcquisitionSource]
		food          bool
		held          map[string]bool
		block, plants bool
	}{
		{"no rows", domain.Known([]policy.AcquisitionSource{bush, hare, oak}), true, nil, false, false},
		{"unknown census", domain.Unknown[[]policy.AcquisitionSource](), true, nil, false, false},
		{"forage hunt-only", designated(bush), true, nil, false, true},
		{"designated hunt", designated(bush, hare), true, nil, true, true},
		{"held hunt", designated(bush, hare), true, map[string]bool{"hare": true}, false, true},
		{"wood tree", designated(oak), false, nil, true, false},
		{"wood ignores food", designated(bush, hare), false, nil, false, false},
		{"food ignores trees", designated(oak), true, nil, false, false},
		{"held tree", designated(oak), false, map[string]bool{"oak": true}, false, false},
	} {
		block, plants := censusBlocking(c.rows, c.food, c.held, nil)
		if block != c.block || (!block && plants != c.plants) {
			t.Errorf("%s: block=%v plants=%v", c.name, block, plants)
		}
	}
	// An undispatched admission blocks like its designated row.
	if block, _ := censusBlocking(domain.Known([]policy.AcquisitionSource{oak}), false, nil, map[string]bool{"oak": true}); !block {
		t.Error("undispatched tree does not block")
	}
	rows, _ := huntRows(domain.Known([]policy.AcquisitionSource{{ID: "bush"}, {ID: "hare", Hunt: true}})).Value()
	if len(rows) != 1 || rows[0].ID != "hare" {
		t.Fatal(rows)
	}
}
