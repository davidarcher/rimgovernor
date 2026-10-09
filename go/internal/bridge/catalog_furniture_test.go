package bridge

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

func furnitureOf(t *testing.T, slice *recordedSlice) policy.RoomFurniture {
	t.Helper()
	shapes, err := slice.catalog().PieceShapes()
	if err != nil {
		t.Fatal(err)
	}
	return shapes.Furniture
}

// The beds rank by sleeping slots, then Comfort per cost (the ones that cost
// something ahead of the free ones), and the same rule picks the double a
// couple is staged first.
func TestRoomFurnitureRanksBedsByComfortPerCost(t *testing.T) {
	slice := buildingsSlice(t)
	f := furnitureOf(t, slice)
	if got, want := f.SleepingBeds(), []string{"Bed", "Bedroll", "SleepingSpot"}; !reflect.DeepEqual(got, want) {
		t.Errorf("sleeping beds %v, want %v", got, want)
	}
	if got, want := f.SleepingLadder(true), []string{"DoubleBed", "Bed", "BedrollDouble", "Bedroll", "RoyalBed", "SleepingSpot"}; !reflect.DeepEqual(got, want) {
		t.Errorf("couple ladder %v, want %v", got, want)
	}
	if f.PrimaryBed() != "Bed" || f.CoupleBed() != "DoubleBed" {
		t.Errorf("primary %q couple %q", f.PrimaryBed(), f.CoupleBed())
	}
	// A hospital bed, a crib-like or small-bodied bed and the animal beds are no
	// adult sleeping bed.
	if slices.Contains(f.SleepingBeds(), "HospitalBed") || slices.Contains(f.SleepingBeds(), "AnimalBed") {
		t.Errorf("sleeping beds hold a bed that is not one: %v", f.SleepingBeds())
	}
	// A cheaper bed with the same comfort outranks the dearer one, whatever
	// their names say.
	slice.copyThing("Bed", "ZCot")
	slice.scaleCosts("ZCot", 1, 4)
	if got := furnitureOf(t, slice).PrimaryBed(); got != "ZCot" {
		t.Errorf("primary bed %q, want the cheaper ZCot", got)
	}
}

func TestRoomFurnitureAnimalBedsSarcophagusAndBenches(t *testing.T) {
	f := furnitureOf(t, buildingsSlice(t))
	if f.AnimalSpot != "AnimalSleepingSpot" || f.AnimalBed != "AnimalBed" || f.Sarcophagus != "Sarcophagus" {
		t.Errorf("animal spot %q bed %q sarcophagus %q", f.AnimalSpot, f.AnimalBed, f.Sarcophagus)
	}
	want := map[policy.RoomRole]string{policy.RoomRoleKitchen: "FueledStove", policy.RoomRoleWorkshop: "HandTailoringBench", policy.RoomRoleLaboratory: "SimpleResearchBench"}
	if !reflect.DeepEqual(f.Bench, want) {
		t.Errorf("benches %v, want the cheapest of each role %v", f.Bench, want)
	}
}

// A facility is chosen by what the bed's or bench's links offer, the most
// offset per cost, and only the ones whose placement rules fit the slot.
func TestRoomFurnitureFacilitiesAreChosenFromTheLinks(t *testing.T) {
	slice := buildingsSlice(t)
	f := furnitureOf(t, slice)
	if f.EndTable.Def != "EndTable" || !f.EndTable.CardinalToHead || f.Dresser.Def != "Dresser" || f.Dresser.Adjacent || f.Cabinet.Def != "ToolCabinet" || f.Cabinet.MaxSimultaneous != 2 || f.Monitor.Def != "VitalsMonitor" || !f.Monitor.Adjacent {
		t.Errorf("facilities %+v", f.Facilities())
	}
	// A better table the bed does not link is ignored; one it links with more
	// offset per cost wins.
	fancy := slice.copyThing("EndTable", "FancyTable")
	slice.scaleCosts("FancyTable", 1, 4)
	for _, offset := range exactCompOf[*d.CompProperties_Facility](t, fancy).GetStatOffsets() {
		offset.GetValue().Value *= 10
	}
	if got := furnitureOf(t, slice).EndTable.Def; got != "EndTable" {
		t.Errorf("end table %q, an unlinked facility must not win", got)
	}
	links := exactCompOf[*d.CompProperties_AffectedByFacilities](t, slice.thing("Bed"))
	links.LinkableFacilities = append(links.LinkableFacilities, "FancyTable")
	if got := furnitureOf(t, slice).EndTable.Def; got != "FancyTable" {
		t.Errorf("end table %q, want the linked better one", got)
	}
}

func exactCompOf[T proto.Message](t *testing.T, row *d.ThingDef) T {
	t.Helper()
	comp, ok := exactComp[T](row)
	if !ok {
		t.Fatalf("%s has no %T", row.GetDefName(), comp)
	}
	return comp
}

// The climate heater is the cheapest def that heats from a power draw:
// a cooler (negative energy) and an unpowered def never match, whatever the names.
func TestRoomFurnitureHeaterIsChosenByTempControlRule(t *testing.T) {
	slice := buildingsSlice(t)
	if got := furnitureOf(t, slice).Heater; got != "Heater" {
		t.Errorf("heater %q", got)
	}
	slice.copyThing("Heater", "ZRadiator")
	slice.scaleCosts("ZRadiator", 1, 4)
	if got := furnitureOf(t, slice).Heater; got != "ZRadiator" {
		t.Errorf("heater %q, want the cheaper ZRadiator", got)
	}
}

// The animal flap is the door roamers can open, by that property and never by
// name; a plain door is none.
func TestRoomFurnitureAnimalFlapIsTheDoorRoamersCanOpen(t *testing.T) {
	slice := buildingsSlice(t)
	if got := furnitureOf(t, slice).AnimalFlap; got != "AnimalFlap" {
		t.Errorf("animal flap %q", got)
	}
	slice.copyThing("AnimalFlap", "ZCurtain")
	slice.drop("AnimalFlap")
	if got := furnitureOf(t, slice).AnimalFlap; got != "ZCurtain" {
		t.Errorf("animal flap %q, want the renamed ZCurtain", got)
	}
}
