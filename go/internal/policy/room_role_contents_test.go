package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestObservedRoomContentsSkipsRoomsItCannotRead(t *testing.T) {
	cat := roleTestCatalog()
	region := RectRegion(Rectangle{X: 0, Z: 0, Width: 3, Height: 3})
	plan := LayoutPlan{Rooms: []PlannedRoom{{ID: 1, Role: PlannedBedroom, Region: region}}}
	rooms := RoomObservation{Rooms: []Room{{ID: "r", Enclosed: domain.Known(true), Cells: region.Cells()}}}
	at := domain.Cell{X: 1, Z: 1}
	bed := func(id, def string) CurrentBuilding {
		b, err := domain.NewBuilding(def, at, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		return CurrentBuilding{ID: id, Building: b, Cells: []domain.Cell{at}}
	}
	known := SleepingObservation{
		People: []SleepingPerson{{ID: "a", Partners: []PawnID{"b"}}, {ID: "b", Partners: []PawnID{"a"}}},
		Beds:   []SleepingBed{{ID: "x", Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []PawnID{"a", "c"}}},
	}
	got := ObservedRoomContents(cat, plan, rooms, []CurrentBuilding{bed("x", "Bed")}, known)
	if len(got) != 1 || len(got[0].Pieces) != 1 || got[0].Pieces[0].State != CondBedFree {
		t.Fatalf("contents = %+v", got)
	}
	owners := got[0].Pieces[0].Owners
	if len(owners) != 2 || len(owners[0].Cluster) != 2 || len(owners[1].Cluster) != 1 {
		t.Fatalf("owners = %+v, want a in the a-b cluster and c alone", owners)
	}
	unknownState := SleepingObservation{Beds: []SleepingBed{{ID: "x", Medical: domain.Unknown[bool](), Prisoners: domain.Known(false)}}}
	if got := ObservedRoomContents(cat, plan, rooms, []CurrentBuilding{bed("x", "Bed")}, unknownState); got != nil {
		t.Fatalf("a bed of unknown state was compared: %+v", got)
	}
	if got := ObservedRoomContents(cat, plan, rooms, []CurrentBuilding{bed("x", "Bed")}, SleepingObservation{}); got != nil {
		t.Fatalf("a bed the sleeping census does not name was compared: %+v", got)
	}
	if got := ObservedRoomContents(cat, plan, rooms, []CurrentBuilding{bed("y", "Altar")}, known); got != nil {
		t.Fatalf("an altar room was compared: %+v", got)
	}
	if got := ObservedRoomContents(cat, plan, RoomObservation{}, []CurrentBuilding{bed("x", "Bed")}, known); got != nil {
		t.Fatalf("a room not standing was compared: %+v", got)
	}
}
