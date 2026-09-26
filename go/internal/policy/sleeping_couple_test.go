package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// coupleFixture: a and b own single beds in rooms r1 and r2, sleeping in
// them; a vacant double bed stands in r3.
func coupleFixture(partners bool) SleepingObservation {
	bed := func(id string, def Resource, room string, owners ...PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: def, Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Roofed: domain.Known(true), RestEffectiveness: domain.Known(1.0), Temperature: domain.Known(20.0), Owners: owners, Users: owners, AccessibleTo: []PawnID{"a", "b"}, Room: domain.Known(room)}
	}
	person := func(id, bed string, partner PawnID) SleepingPerson {
		p := SleepingPerson{ID: PawnID(id), OwnedBed: domain.Known(bed), ComfortableMin: domain.Known(10.0), ComfortableMax: domain.Known(30.0), BedSharingAllowed: domain.Known(true)}
		if partners {
			p.Partners = []PawnID{partner}
		}
		return p
	}
	return SleepingObservation{Colonists: 2,
		People: []SleepingPerson{person("a", "s1", "b"), person("b", "s2", "a")},
		Beds:   []SleepingBed{bed("s1", "Bed", "r1", "a"), bed("s2", "Bed", "r2", "b"), bed("d", "DoubleBed", "r3")}}
}

func sleepingTargets(t *testing.T, v SleepingObservation) []SleepingTarget {
	t.Helper()
	r, err := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
	rows, known := r.Targets.Value()
	if err != nil || !known {
		t.Fatal(r, err)
	}
	return rows
}

func TestSleepingCoupleSharesDoubleBed(t *testing.T) {
	v := coupleFixture(true)
	rows := sleepingTargets(t, v)
	if len(rows) != 2 || rows[0].Kind != SleepingShare || rows[0].Partner != "b" || len(rows[0].Available) != 1 || rows[0].Available[0] != "d" {
		t.Fatal(rows)
	}
	choice, err := SelectSleepingMethod(SleepingRequest{Targets: domain.Known(rows)})
	if err != nil || choice.Method != SleepingAssign || choice.Pawn != "a" || choice.Bed != "d" || choice.PreviousBed != "s1" {
		t.Fatal(choice, err)
	}
	// a moves into the double bed and waits; b is offered to join it.
	v.Beds[0].Owners, v.Beds[0].Users = nil, nil
	v.Beds[2].Owners = []PawnID{"a"}
	v.People[0].OwnedBed = domain.Known("d")
	rows = sleepingTargets(t, v)
	if len(rows) != 2 || rows[0].Kind != SleepingUseNeeded || rows[1].Kind != SleepingShare || len(rows[1].Available) != 1 || rows[1].Available[0] != "d" {
		t.Fatal(rows)
	}
	// Both own it and sleep there: the need clears.
	v.Beds[1].Owners, v.Beds[1].Users = nil, nil
	v.Beds[2].Owners, v.Beds[2].Users = []PawnID{"a", "b"}, []PawnID{"a", "b"}
	v.People[1].OwnedBed = domain.Known("d")
	if rows = sleepingTargets(t, v); len(rows) != 0 {
		t.Fatal(rows)
	}
	// The relationship breaks: each needs a bed of its own again.
	v.People[0].Partners, v.People[1].Partners = nil, nil
	rows = sleepingTargets(t, v)
	if len(rows) != 2 || rows[0].Kind != SleepingUpgrade || rows[0].Partner != "" || len(rows[0].Available) != 2 || rows[0].Available[0] != "s1" {
		t.Fatal(rows)
	}
}

func TestSleepingCoupleSinglesInOneRoom(t *testing.T) {
	v := coupleFixture(true)
	v.Beds[2] = v.Beds[0]
	v.Beds[2].ID, v.Beds[2].Owners, v.Beds[2].Users, v.Beds[2].Room = "s3", nil, nil, domain.Known("r2")
	rows := sleepingTargets(t, v)
	if len(rows) != 2 || len(rows[0].Available) != 1 || rows[0].Available[0] != "s3" {
		t.Fatal(rows)
	}
	// Two singles in one room satisfy the couple.
	v.Beds[0].Room = domain.Known("r2")
	if rows = sleepingTargets(t, v); len(rows) != 0 {
		t.Fatal(rows)
	}
}

func TestSleepingCoupleBuildsDoubleBed(t *testing.T) {
	v := coupleFixture(true)
	v.Beds = v.Beds[:2]
	rows := sleepingTargets(t, v)
	if len(rows) != 2 || rows[0].Kind != SleepingShare || len(rows[0].Available) != 0 {
		t.Fatal(rows)
	}
	rooms := RoomObservation{Rooms: []Room{{Role: domain.Known(RoomRoleBedroom), Temperature: domain.Known(20.0), Cells: []domain.Cell{{X: 1, Z: 1}}}}}
	defs := []BenchDefinition{{Name: "DoubleBed", Available: domain.Known(true)}, {Name: "Bed", Available: domain.Known(true)}}
	choice, err := SelectSleepingMethod(SleepingRequest{Targets: domain.Known(rows), Sleeping: domain.Known(v), Rooms: domain.Known(rooms), Definitions: defs})
	if err != nil || choice.Method != SleepingBuild || choice.Definition != "DoubleBed" {
		t.Fatal(choice, err)
	}
}

func TestSleepingNeverPairsNonPartners(t *testing.T) {
	// Not partners: each keeps its own single, nothing to do.
	v := coupleFixture(false)
	if rows := sleepingTargets(t, v); len(rows) != 0 {
		t.Fatal(rows)
	}
	// Unwilling or one-sided partners are not a couple either.
	for _, mutate := range []func(*SleepingObservation){
		func(v *SleepingObservation) { v.People[1].BedSharingAllowed = domain.Known(false) },
		func(v *SleepingObservation) { v.People[1].BedSharingAllowed = domain.Unknown[bool]() },
		func(v *SleepingObservation) { v.People[1].Partners = nil },
	} {
		v := coupleFixture(true)
		mutate(&v)
		if rows := sleepingTargets(t, v); len(rows) != 0 {
			t.Fatal(rows)
		}
	}
	// A double bed half-owned by a stranger is offered to nobody else.
	v = coupleFixture(false)
	v.People[0].OwnedBed = domain.Known("")
	v.Beds[0].Owners, v.Beds[0].Users = nil, nil
	v.Beds[2].Owners = []PawnID{"b"}
	v.Beds = []SleepingBed{v.Beds[2]}
	v.People[1].OwnedBed = domain.Known("d")
	rows := sleepingTargets(t, v)
	if len(rows) != 2 || len(rows[0].Available) != 0 {
		t.Fatal(rows)
	}
}
