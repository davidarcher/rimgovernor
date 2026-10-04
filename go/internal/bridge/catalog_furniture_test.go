package bridge

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func furnitureOf(t *testing.T, defs ...FixtureDef) policy.RoomFurniture {
	t.Helper()
	shapes, err := FixtureCatalog("load", defs...).PieceShapes()
	if err != nil {
		t.Fatal(err)
	}
	return shapes.Furniture
}

// The beds rank by sleeping slots, then Comfort per cost (the ones that cost
// something ahead of the free ones), and the same rule picks the double a
// couple is staged first.
func TestRoomFurnitureRanksBedsByComfortPerCost(t *testing.T) {
	f := furnitureOf(t, CoreFurnitureFixtures()...)
	if got, want := f.SleepingBeds(), []string{"Bed", "Bedroll", "SleepingSpot"}; !reflect.DeepEqual(got, want) {
		t.Errorf("sleeping beds %v, want %v", got, want)
	}
	if got, want := f.SleepingLadder(true), []string{"DoubleBed", "Bed", "BedrollDouble", "Bedroll", "RoyalBed", "SleepingSpot"}; !reflect.DeepEqual(got, want) {
		t.Errorf("couple ladder %v, want %v", got, want)
	}
	if f.PrimaryBed() != "Bed" || f.CoupleBed() != "DoubleBed" {
		t.Errorf("primary %q couple %q", f.PrimaryBed(), f.CoupleBed())
	}
	// A cheaper bed with the same comfort outranks the dearer one, whatever
	// their names say.
	cheap := FixtureDef{Name: "ZCot", Width: 1, Height: 2, Bed: true, Comfort: .75, Costs: []policy.Amount{{Resource: "WoodLog", Count: 10}}, Links: []string{"EndTable", "Dresser"}}
	if got := furnitureOf(t, append(CoreFurnitureFixtures(), cheap)...).PrimaryBed(); got != "ZCot" {
		t.Errorf("primary bed %q, want the cheaper ZCot", got)
	}
	// A hospital bed, a crib-like or small-bodied bed and the animal beds are no
	// adult sleeping bed.
	if slices.Contains(f.SleepingBeds(), "HospitalBed") || slices.Contains(f.SleepingBeds(), "AnimalBed") {
		t.Errorf("sleeping beds hold a bed that is not one: %v", f.SleepingBeds())
	}
}

func TestRoomFurnitureAnimalBedsSarcophagusAndBenches(t *testing.T) {
	f := furnitureOf(t, CoreFurnitureFixtures()...)
	if f.AnimalSpot != "AnimalSleepingSpot" || f.AnimalBed != "AnimalBed" || f.Sarcophagus != "Sarcophagus" {
		t.Errorf("animal spot %q bed %q sarcophagus %q", f.AnimalSpot, f.AnimalBed, f.Sarcophagus)
	}
	want := map[policy.RoomRole]string{policy.RoomRoleKitchen: "FueledStove", policy.RoomRoleWorkshop: "TableStonecutter", policy.RoomRoleLaboratory: "SimpleResearchBench"}
	if !reflect.DeepEqual(f.Bench, want) {
		t.Errorf("benches %v, want the cheapest of each role %v", f.Bench, want)
	}
}

// A facility is chosen by what the bed's or bench's links offer, the most
// offset per cost, and only the ones whose placement rules fit the slot.
func TestRoomFurnitureFacilitiesAreChosenFromTheLinks(t *testing.T) {
	f := furnitureOf(t, CoreFurnitureFixtures()...)
	if f.EndTable.Def != "EndTable" || !f.EndTable.CardinalToHead || f.Dresser.Def != "Dresser" || f.Dresser.Adjacent || f.Cabinet.Def != "ToolCabinet" || f.Cabinet.MaxSimultaneous != 2 || f.Monitor.Def != "VitalsMonitor" || !f.Monitor.Adjacent {
		t.Errorf("facilities %+v", f.Facilities())
	}
	// A better table the bed does not link is ignored; one it links with more
	// offset per cost wins.
	unlinked := FixtureDef{Name: "FancyTable", Width: 1, Height: 1, Costs: []policy.Amount{{Resource: "WoodLog", Count: 1}}, Facility: &FixtureFacility{Offsets: map[string]float32{StatComfort: 1}, MaxDistance: 8, MaxSimultaneous: 1, Adjacent: true, CardinalToHead: true}}
	defs := append(CoreFurnitureFixtures(), unlinked)
	if got := furnitureOf(t, defs...).EndTable.Def; got != "EndTable" {
		t.Errorf("end table %q, an unlinked facility must not win", got)
	}
	for i := range defs {
		if defs[i].Name == "Bed" {
			defs[i].Links = append(slices.Clone(defs[i].Links), "FancyTable")
		}
	}
	if got := furnitureOf(t, defs...).EndTable.Def; got != "FancyTable" {
		t.Errorf("end table %q, want the linked better one", got)
	}
}

// The climate heater is the cheapest def that heats from a power draw (#1867):
// a cooler (negative energy) and an unpowered def never match, whatever the names.
func TestRoomFurnitureHeaterIsChosenByTempControlRule(t *testing.T) {
	if got := furnitureOf(t, CoreFurnitureFixtures()...).Heater; got != "Heater" {
		t.Errorf("heater %q", got)
	}
	cheap := FixtureDef{Name: "ZRadiator", Width: 1, Height: 1, Costs: []policy.Amount{{Resource: "WoodLog", Count: 5}}, PowerW: ptr(100.0), TempControlW: ptr(float32(10))}
	if got := furnitureOf(t, append(CoreFurnitureFixtures(), cheap)...).Heater; got != "ZRadiator" {
		t.Errorf("heater %q, want the cheaper ZRadiator", got)
	}
}
