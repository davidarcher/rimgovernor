package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func fieldZoneCells(zones []LayoutZone) int {
	n := 0
	for _, z := range zones {
		if z.Kind != ZoneField {
			continue
		}
		for _, r := range z.Runs {
			n += int(r.Length)
		}
	}
	return n
}

func fieldReplanFacts() StationFacts {
	f := stationFactsFixture()
	f.Survey = soilPatchSurvey()
	f.Plan.Zones = Zone(f.Survey)
	f.Crops = []CropChoice{riceCrop()}
	f.FoodReserveDays = DefaultFoodReserveDays
	return f
}

// A replan takes its field zones from the station advice, not the survey's
// whole-map patch, and the zone follows FieldCapacityTarget when the demand
// changes (#2961).
func TestReplanFieldZoneTracksFieldCapacityTarget(t *testing.T) {
	f := fieldReplanFacts()
	plan, survey := f.Plan, f.Survey
	patch := fieldZoneCells(Zone(survey))
	prev := 0
	for _, reserve := range []float64{DefaultFoodReserveDays, 2 * DefaultFoodReserveDays} {
		target, ok := FieldCapacityTarget(f.Colonists, riceCrop(), reserve).Value()
		if !ok {
			t.Fatal("no capacity target")
		}
		f.Plan, f.FoodReserveDays = plan, reserve
		next, _, err := ReplanLayout(plan, f, survey, nil, RoomGrowth{}, 0)
		if err != nil {
			t.Fatal(err)
		}
		changed := !SamePlan(plan, next)
		got := fieldZoneCells(next.Zones)
		if got < target || got > target+target/10 || got >= patch {
			t.Errorf("reserve %v days: field zone %d cells, target %d, survey patch %d", reserve, got, target, patch)
		}
		if got <= prev || !changed {
			t.Errorf("reserve %v days: field zone %d (was %d) changed=%t, want it grown and recorded", reserve, got, prev, changed)
		}
		prev, plan = got, next
	}
}

// A fresh derive carries the fields of its crops, sized by the capacity
// target, and not the survey's whole-map patch, even from the seed graph.
func TestDeriveCarriesTiledFields(t *testing.T) {
	f := deriveFacts(3, false, false, []float64{10})
	f.Survey = soilPatchSurvey()
	f.Plan.Zones = Zone(f.Survey)
	f.Crops, f.FoodReserveDays = []CropChoice{riceCrop()}, DefaultFoodReserveDays
	fact, advice, err := DeriveLayoutPlan(f.Survey, f, nil, 0)
	if err != nil {
		t.Fatal(err)
	}
	if advice.SeedOnly == "" {
		t.Fatal("want a seed-graph derive")
	}
	plan, ok := fact.Value()
	if !ok {
		t.Fatal("no plan")
	}
	target, _ := FieldCapacityTarget(f.Colonists, riceCrop(), DefaultFoodReserveDays).Value()
	if got := fieldZoneCells(plan.Zones); got < target || got > target+target/10 {
		t.Errorf("derived field zone %d cells, want about the target %d", got, target)
	}
}

// DefaultFieldCrop takes the first crop of its table the catalog offers.
func TestDefaultFieldCropIsFirstAvailableEdible(t *testing.T) {
	rice, potato := riceCrop(), riceCrop()
	potato.Name = "Plant_Potato"
	rice.Available, potato.Available = domain.Known(false), domain.Known(true)
	got, ok := DefaultFieldCrop([]CropChoice{potato, rice})
	if !ok || got.Name != "Plant_Potato" {
		t.Errorf("default crop %q %t, want Plant_Potato", got.Name, ok)
	}
	rice.Available = domain.Known(true)
	if got, _ := DefaultFieldCrop([]CropChoice{potato, rice}); got.Name != "Plant_Rice" {
		t.Errorf("default crop %q, want Plant_Rice", got.Name)
	}
	if _, ok := DefaultFieldCrop(nil); ok {
		t.Error("a default crop with no choices")
	}
}
