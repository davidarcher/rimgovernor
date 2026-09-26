package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func replacementBed(id, quality string, owners ...PawnID) SleepingBed {
	return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Room: domain.Known("Room_1"), Quality: domain.Known(quality), Owners: owners, Cell: domain.Cell{X: 2, Z: 3}}
}

func TestBedReplacementBuildsAssignsThenRemoves(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	obs, rooms, _ := upgradeFixture(t, low)
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	all := func(string) bool { return true }

	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Poor", "a")}
	u, ok := NextBedReplacement(obs, targets, rooms, all)
	if !ok || u.Step != BedReplaceBuild || u.Def != "Bed" {
		t.Fatalf("build = %+v %v", u, ok)
	}
	r := OccupiedRect(u.Cell, domain.Cell{X: 1, Z: 2}, u.Rot)
	if roomPieceOverlaps(rooms[0].Pieces, r) {
		t.Fatalf("new bed %v overlaps the old", r)
	}
	if _, ok := NextBedReplacement(obs, targets, rooms, func(d string) bool { return d != "Bed" }); ok {
		t.Fatal("no bed buildable: nothing due")
	}

	// The new bed stands: the owner moves onto it.
	obs.Beds = append(obs.Beds, replacementBed("Bed_2", "Good"))
	if u, ok := NextBedReplacement(obs, targets, rooms, all); !ok || u.Step != BedReplaceAssign || u.Pawn != "a" || u.Bed != "Bed_2" || u.PreviousBed != "Bed_1" {
		t.Fatalf("assign = %+v %v", u, ok)
	}

	// Moved: the old bed goes.
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Poor"), replacementBed("Bed_2", "Good", "a")}
	if u, ok := NextBedReplacement(obs, targets, rooms, all); !ok || u.Step != BedReplaceRemove || u.Bed != "Bed_1" || u.Def != "Bed" || u.Cell != (domain.Cell{X: 2, Z: 3}) {
		t.Fatalf("remove = %+v %v", u, ok)
	}

	// A new bed no better than the old is never moved onto: it goes.
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Poor", "a"), replacementBed("Bed_2", "Poor")}
	if u, ok := NextBedReplacement(obs, targets, rooms, all); !ok || u.Step != BedReplaceRemove || u.Bed != "Bed_2" {
		t.Fatalf("equal spare = %+v %v", u, ok)
	}
}

func TestBedReplacementLeavesGoodBedsAndMetTargets(t *testing.T) {
	low := RoomQuality{Wealth: 300, Beauty: 1, Space: 25, Cleanliness: 0, Impressiveness: 35}
	all := func(string) bool { return true }
	obs, rooms, _ := upgradeFixture(t, low)
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Normal", "a")}
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	if u, ok := NextBedReplacement(obs, targets, rooms, all); ok {
		t.Fatalf("normal bed = %+v", u)
	}
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Awful", "a")}
	targets["Room_1"] = RoomTarget{Room: "Room_1", Min: ImpressivenessMediocre}
	if u, ok := NextBedReplacement(obs, targets, rooms, all); ok {
		t.Fatalf("met target = %+v", u)
	}
}
