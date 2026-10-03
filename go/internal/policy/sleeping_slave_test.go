package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// slaveFixture is sleepingFixture's housed colonist plus one slave and a
// vacant colonist bed "spare" the slave can reach.
func slaveFixture() SleepingObservation {
	v := sleepingFixture(true)
	v.Slaves = []SleepingPerson{{ID: "slave", OwnedBed: domain.Known(""), ComfortableMin: domain.Known(10.0), ComfortableMax: domain.Known(30.0)}}
	spare := v.Beds[0]
	spare.ID, spare.Owners, spare.Users, spare.AccessibleTo = "spare", nil, nil, []PawnID{"pawn", "slave"}
	v.Beds = append(v.Beds, spare)
	return v
}

func TestSleepingSlaveNeedsSlaveBed(t *testing.T) {
	v := slaveFixture()
	r, err := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
	rows, known := r.Targets.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].Pawn != "slave" || len(rows[0].Available) != 0 {
		t.Fatalf("a colonist bed is no slave bed: %+v %v", rows, err)
	}
	choice, err := SelectSleepingMethod(SleepingRequest{Furniture: testFurniture, Targets: r.Targets, Sleeping: domain.Known(v)})
	if err != nil || choice.Method != SleepingMarkSlaves || choice.Pawn != "slave" || choice.Bed != "spare" {
		t.Fatalf("choice %+v %v", choice, err)
	}
	v.Beds[1].Slaves = true
	r, _ = ReviewSleeping(domain.Known(v), SleepingHistory{}, 2)
	rows, _ = r.Targets.Value()
	if len(rows) != 1 || rows[0].Pawn != "slave" || len(rows[0].Available) != 1 || rows[0].Available[0] != "spare" {
		t.Fatalf("slave bed offered: %+v", rows)
	}
	v.Slaves[0].OwnedBed = domain.Known("spare")
	v.Beds[1].Owners, v.Beds[1].Users = []PawnID{"slave"}, []PawnID{"slave"}
	r, _ = ReviewSleeping(domain.Known(v), SleepingHistory{}, 3)
	if recovered, known := r.Recovered().Value(); !known || !recovered {
		t.Fatalf("slave housed: %+v", r)
	}
}

func TestSleepingColonistNeverTakesSlaveBed(t *testing.T) {
	v := slaveFixture()
	v.Slaves = nil
	v.Beds[0].Slaves = true
	r, _ := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
	rows, _ := r.Targets.Value()
	if len(rows) != 1 || rows[0].Kind != SleepingUnsafe || len(rows[0].Available) != 1 || rows[0].Available[0] != "spare" {
		t.Fatalf("colonist in a slave bed moves out: %+v", rows)
	}
}

func TestSleepingSlaveWithoutSpareBedBuilds(t *testing.T) {
	v := slaveFixture()
	v.Beds = v.Beds[:1]
	r, _ := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
	rooms := domain.Known(RoomObservation{Shapes: testShapes, Rooms: []Room{sleepingRoom("warm", RoomRoleBedroom, 20)}})
	defs := []BenchDefinition{{Name: "Bed", Available: domain.Known(true)}}
	choice, err := SelectSleepingMethod(SleepingRequest{Furniture: testFurniture, Targets: r.Targets, Sleeping: domain.Known(v), Rooms: rooms, Definitions: defs})
	if err != nil || choice.Method != SleepingBuild || choice.Unhoused != 1 {
		t.Fatalf("choice %+v %v", choice, err)
	}
}
