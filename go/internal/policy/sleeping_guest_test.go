package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func guestBed(id string, def Resource, room string, who ...PawnID) SleepingBed {
	return SleepingBed{ID: id, Definition: def, Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Roofed: domain.Known(true), RestEffectiveness: domain.Known(1.0), Temperature: domain.Known(20.0), AccessibleTo: who, Room: domain.Known(room)}
}

func guestCensus(title *RoyalTitle) SleepingObservation {
	who := []PawnID{"guest", "visitor"}
	quality := func(i float64) domain.Fact[RoomQuality] { return domain.Known(RoomQuality{Impressiveness: i}) }
	return SleepingObservation{
		Guests: []SleepingPerson{{ID: "guest", OwnedBed: domain.Known(""), ComfortableMin: domain.Known(10.0), ComfortableMax: domain.Known(30.0), Title: title}},
		Beds:   []SleepingBed{guestBed("a-plain", "Bed", "small", who...), guestBed("b-royal", "RoyalBed", "suite", who...)},
		Rooms: domain.Known([]UpkeepRoom{
			{ID: "small", Quality: quality(10), Cells: domain.Known(8)},
			{ID: "suite", Quality: quality(60), Cells: domain.Known(30)},
		}),
	}
}

func guestAssignment(t *testing.T, v SleepingObservation) (SleepingTarget, SleepingChoice) {
	t.Helper()
	r, err := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
	rows, known := r.Targets.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].Pawn != "guest" {
		t.Fatal(r, err)
	}
	choice, err := SelectSleepingMethod(SleepingRequest{Targets: r.Targets, Sleeping: domain.Known(v)})
	if err != nil {
		t.Fatal(err)
	}
	return rows[0], choice
}

func TestSleepingTitledGuestGetsTitleBed(t *testing.T) {
	title := &RoyalTitle{Definition: "Knight", BedroomMinArea: 20, BedroomMinImpressiveness: 40, BedroomThings: []BedroomThing{{AnyOf: []Resource{"RoyalBed"}, Count: 1}}}
	target, choice := guestAssignment(t, guestCensus(title))
	if target.Available[0] != "b-royal" || choice.Method != SleepingAssign || choice.Pawn != "guest" || choice.Bed != "b-royal" {
		t.Fatal(target, choice)
	}
}

func TestSleepingUntitledGuestGetsAnySafeBed(t *testing.T) {
	target, choice := guestAssignment(t, guestCensus(nil))
	if len(target.Available) != 2 || choice.Method != SleepingAssign || choice.Bed != "a-plain" {
		t.Fatal(target, choice)
	}
}

func TestSleepingVisitorNotInCensusIsNeverTargeted(t *testing.T) {
	v := guestCensus(nil)
	v.Guests = nil // "visitor" can reach the beds but is not hosted
	r, err := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
	if rows, known := r.Targets.Value(); err != nil || !known || len(rows) != 0 {
		t.Fatal(r, err)
	}
}

func TestSleepingGuestNeverCausesABuild(t *testing.T) {
	v := guestCensus(nil)
	v.Beds = nil
	r, err := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
	if rows, known := r.Targets.Value(); err != nil || !known || len(rows) != 0 {
		t.Fatal(r, err)
	}
}
