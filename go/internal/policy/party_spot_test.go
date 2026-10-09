package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var partySpotLevels = ImpressivenessLevels{Stages: []float64{0, 20, 30, 40, 50, 65}}

func partySpotRoom(id string, role RoomRole, impressiveness float64, x, size int32) (Room, UpkeepRoom, []SiteCell) {
	var cells []domain.Cell
	var site []SiteCell
	for i := int32(0); i < size; i++ {
		c := domain.Cell{X: x + i, Z: 0}
		cells = append(cells, c)
		site = append(site, SiteCell{Cell: c, Roofed: domain.Known(true), Indoors: domain.Known(true)})
	}
	return Room{ID: id, Role: domain.Known(role), Cells: cells},
		UpkeepRoom{ID: id, Role: string(role), Quality: domain.Known(RoomQuality{Impressiveness: impressiveness})}, site
}

func partySpotSpotAt(t *testing.T, id string, cell domain.Cell) CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding(PartySpotDefinition, cell, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: id, Building: b, Cells: []domain.Cell{cell}}
}

type partySpotRoomSpec struct {
	id             string
	role           RoomRole
	impressiveness float64
	x, size        int32
}

func partySpotInput(t *testing.T, specs []partySpotRoomSpec, spots ...CurrentBuilding) PartySpotInput {
	t.Helper()
	var rooms []Room
	var qualities []UpkeepRoom
	var cells []SiteCell
	for _, s := range specs {
		r, q, c := partySpotRoom(s.id, s.role, s.impressiveness, s.x, s.size)
		rooms, qualities, cells = append(rooms, r), append(qualities, q), append(cells, c...)
	}
	return PartySpotInput{
		Rooms: domain.Known(RoomObservation{Rooms: rooms}), Qualities: domain.Known(qualities), Levels: partySpotLevels,
		Census: domain.Known(CurrentConstruction{Colony: true, Buildings: spots}), Cells: cells, Placeable: true,
	}
}

func partySpotNeed(t *testing.T, in PartySpotInput) PartySpotNeed {
	t.Helper()
	need, known := ReviewPartySpot(in).Value()
	if !known {
		t.Fatal("unknown")
	}
	return need
}

func TestPartySpotPlacedInBestRoomByStageThenSize(t *testing.T) {
	in := partySpotInput(t, []partySpotRoomSpec{
		{"big", RoomRoleDiningRoom, 31, 0, 6}, {"fine", RoomRoleRecRoom, 45, 10, 3}, {"also", RoomRoleRecRoom, 42, 20, 5}, {"bed", RoomRoleBedroom, 90, 30, 9},
	})
	need := partySpotNeed(t, in)
	// "fine" and "also" share the decent stage; the larger room wins the tie.
	if !need.Place || need.Room != "also" || len(need.Cells) != 5 {
		t.Fatalf("%+v", need)
	}
}

func TestPartySpotWithoutSharedRoomPlacesAnywhere(t *testing.T) {
	in := partySpotInput(t, []partySpotRoomSpec{{"bed", RoomRoleBedroom, 90, 0, 4}})
	if need := partySpotNeed(t, in); !need.Place || need.Room != "" || need.Cells != nil {
		t.Fatalf("%+v", need)
	}
	in.Cells = nil
	if need := partySpotNeed(t, in); need.Owed() {
		t.Fatalf("no valid cell must owe nothing: %+v", need)
	}
	in = partySpotInput(t, []partySpotRoomSpec{{"bed", RoomRoleBedroom, 90, 0, 4}})
	in.Placeable = false
	if need := partySpotNeed(t, in); need.Owed() {
		t.Fatalf("an unbuildable spot must owe nothing: %+v", need)
	}
}

func TestPartySpotMovesOnlyToStrictlyHigherStage(t *testing.T) {
	specs := []partySpotRoomSpec{{"a", RoomRoleDiningRoom, 41, 0, 4}, {"b", RoomRoleRecRoom, 48, 10, 9}}
	spot := partySpotSpotAt(t, "spot", domain.Cell{X: 0})
	if need := partySpotNeed(t, partySpotInput(t, specs, spot)); need.Owed() {
		t.Fatalf("same stage must not move: %+v", need)
	}
	specs[1].impressiveness = 55
	need := partySpotNeed(t, partySpotInput(t, specs, spot))
	if !need.HasRetire || need.Retire.ID != "spot" || need.Place {
		t.Fatalf("%+v", need)
	}
	// The retirement is spent: the deficit is dropped, not held.
	in := partySpotInput(t, specs, spot)
	in.Spent = func(retire bool, id string) bool { return retire && id == "spot" }
	if need := partySpotNeed(t, in); need.Owed() {
		t.Fatalf("%+v", need)
	}
	// With the old spot gone the new one is placed in the better room.
	if need := partySpotNeed(t, partySpotInput(t, specs)); !need.Place || need.Room != "b" {
		t.Fatalf("%+v", need)
	}
}

func TestPartySpotDuplicatesKeepTheBestRoomsSpot(t *testing.T) {
	specs := []partySpotRoomSpec{{"a", RoomRoleDiningRoom, 41, 0, 4}, {"b", RoomRoleRecRoom, 55, 10, 4}}
	low, high := partySpotSpotAt(t, "low", domain.Cell{X: 0}), partySpotSpotAt(t, "high", domain.Cell{X: 10})
	need := partySpotNeed(t, partySpotInput(t, specs, low, high))
	if !need.HasRetire || need.Retire.ID != "low" {
		t.Fatalf("%+v", need)
	}
	in := partySpotInput(t, specs, low, high)
	in.Spent = func(bool, string) bool { return true }
	if need := partySpotNeed(t, in); need.Owed() {
		t.Fatalf("a spent one-shot must drop the deficit: %+v", need)
	}
	if need := partySpotNeed(t, partySpotInput(t, specs, high)); need.Owed() {
		t.Fatalf("exactly one spot in the best room owes nothing: %+v", need)
	}
}

func TestPartySpotPendingSiteAndUnknownOweNothing(t *testing.T) {
	in := partySpotInput(t, []partySpotRoomSpec{{"a", RoomRoleDiningRoom, 41, 0, 4}})
	b, _ := domain.NewBuilding(PartySpotDefinition, domain.Cell{X: 1}, domain.North, "")
	in.Census = domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{{Building: b, Stage: "blueprint"}}})
	if need := partySpotNeed(t, in); need.Owed() {
		t.Fatalf("%+v", need)
	}
	in.Qualities = domain.Unknown[[]UpkeepRoom]()
	if _, known := ReviewPartySpot(in).Value(); known {
		t.Fatal("unknown qualities must leave the need unknown")
	}
}

func TestPartySpotOwedHoldsComfortUnmetOnlyWhileOwed(t *testing.T) {
	f := stableRounds()
	f.PartySpotOwed = domain.Known(true)
	r := needs(t, f, RoundsLatches{})
	if !hasNeed(r, EnsureComfort) || !assessedDeficit(r, EnsureComfort) {
		t.Fatalf("%+v", r)
	}
	f.PartySpotOwed = domain.Known(false)
	if r := needs(t, f, RoundsLatches{}); hasNeed(r, EnsureComfort) || assessedDeficit(r, EnsureComfort) {
		t.Fatalf("%+v", r)
	}
	f.PartySpotOwed = domain.Unknown[bool]()
	if r := needs(t, f, RoundsLatches{}); hasNeed(r, EnsureComfort) || assessedDeficit(r, EnsureComfort) {
		t.Fatalf("an unknown spot must leave comfort as it was: %+v", r)
	}
}

func TestImpressivenessStage(t *testing.T) {
	for score, want := range map[float64]int{-5: 0, 19: 0, 20: 1, 49: 3, 50: 4, 64: 4, 65: 5, 200: 5} {
		if got := partySpotLevels.Stage(score); got != want {
			t.Errorf("Stage(%v) = %d, want %d", score, got, want)
		}
	}
	named := ImpressivenessLevels{Dull: 20, Mediocre: 30, Decent: 40, SlightlyImpressive: 50}
	if named.Stage(45) != 3 || named.Stage(80) != 4 {
		t.Error("named levels")
	}
}
