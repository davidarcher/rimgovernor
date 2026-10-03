package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCompanionMasterIsTheFirstBondedRosterPartner(t *testing.T) {
	people := []SleepingPerson{{ID: "b"}, {ID: "c"}}
	if m, ok := CompanionMaster(UpkeepAnimal{BondedPawns: []string{"z", "c", "b"}}, people); !ok || m != "b" {
		t.Fatal("first roster partner by id", m, ok)
	}
	if _, ok := CompanionMaster(UpkeepAnimal{BondedPawns: []string{"z"}}, people); ok {
		t.Fatal("a partner off the roster is no master")
	}
}

func TestCompanionBedGoesInTheMastersSoloBedroom(t *testing.T) {
	obs, rooms, _ := upgradeFixture(t, RoomQuality{Impressiveness: 80})
	obs.People = []SleepingPerson{{ID: "a"}}
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Good", "a")}
	spot := InteriorPieceDef{Def: AnimalSleepingSpotDefinition, Size: domain.Cell{X: 1, Z: 1}}
	dog := UpkeepAnimal{ID: "dog", BondedPawns: []string{"a"}}
	stray := UpkeepAnimal{ID: "stray"}

	if _, ok := NextCompanionBed(obs, []UpkeepAnimal{stray}, rooms, spot, true); ok {
		t.Fatal("an unbonded animal owes no bed")
	}
	if _, ok := NextCompanionBed(obs, []UpkeepAnimal{dog}, rooms, spot, false); ok {
		t.Fatal("an unavailable definition owes nothing")
	}
	u, ok := NextCompanionBed(obs, []UpkeepAnimal{dog}, rooms, spot, true)
	if !ok || u.Room != "Room_1" || u.Def != AnimalSleepingSpotDefinition || !rectInside(rooms[0].Room.Interior, OccupiedRect(u.Anchor, spot.Size, u.Rot)) {
		t.Fatal("companion bed in the master's room", u, ok)
	}
	rooms[0].Pieces = append(rooms[0].Pieces, TidyPiece{Def: u.Def, Rect: OccupiedRect(u.Anchor, spot.Size, u.Rot)})
	if _, ok := NextCompanionBed(obs, []UpkeepAnimal{dog}, rooms, spot, true); ok {
		t.Fatal("one bed per mastered animal")
	}
	if u, ok := NextCompanionBed(obs, []UpkeepAnimal{dog, {ID: "cat", BondedPawns: []string{"a"}}}, rooms, spot, true); !ok || u.Slot != "companion-bed-2" {
		t.Fatal("a second animal gets a second bed", u, ok)
	}
	// A shared bedroom is nobody's solo room.
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Good", "a", "b")}
	if _, ok := NextCompanionBed(obs, []UpkeepAnimal{{ID: "dog", BondedPawns: []string{"a"}}}, []TidyRoom{{ID: "Room_1", Room: rooms[0].Room}}, spot, true); ok {
		t.Fatal("a shared bedroom gets no companion bed")
	}
}
