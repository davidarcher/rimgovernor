package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rescuerCandidate(id domain.PawnID) RescuerFacts {
	return RescuerFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
}
func rescuePatientCandidate(id domain.PawnID, downed, inBed bool) RescuePatientFacts {
	return RescuePatientFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(downed), InBed: domain.Known(inBed), ExistingJobDef: domain.Known("")}
}

func TestSelectRescueExcludesIneligibleCandidates(t *testing.T) {
	busy := rescuerCandidate("busy")
	busy.ExistingJobDef = domain.Known("Rescue")
	drafted := rescuerCandidate("drafted")
	drafted.Drafted = domain.Known(true)
	fine := rescuerCandidate("fine")
	standing := rescuePatientCandidate("standing", false, false)
	alreadyBedded := rescuePatientCandidate("bedded", true, true)
	downed := rescuePatientCandidate("downed", true, false)

	rescuer, patient, ok := SelectRescue([]RescuerFacts{busy, drafted, fine}, []RescuePatientFacts{standing, alreadyBedded, downed})
	if !ok || rescuer != "fine" || patient != "downed" {
		t.Fatal(rescuer, patient, ok)
	}
	if _, _, ok := SelectRescue([]RescuerFacts{busy, drafted}, []RescuePatientFacts{downed}); ok {
		t.Fatal("selected an ineligible rescuer")
	}
	if _, _, ok := SelectRescue([]RescuerFacts{fine}, []RescuePatientFacts{standing, alreadyBedded}); ok {
		t.Fatal("selected an ineligible patient")
	}
}

func TestSelectRescueUsesForcedAndQueuedPawnAsFallback(t *testing.T) {
	forced, idle := rescuerCandidate("a"), rescuerCandidate("z")
	forced.PlayerForced, forced.QueuedJobs = domain.Known(true), domain.Known(uint32(2))
	patients := []RescuePatientFacts{rescuePatientCandidate("patient", true, false)}
	if pawn, _, ok := SelectRescue([]RescuerFacts{forced, idle}, patients); !ok || pawn != idle.Pawn {
		t.Fatal(pawn, ok)
	}
	if pawn, _, ok := SelectRescue([]RescuerFacts{forced}, patients); !ok || pawn != forced.Pawn {
		t.Fatal(pawn, ok)
	}
	forced.PlayerForced, forced.QueuedJobs = domain.Unknown[bool](), domain.Unknown[uint32]()
	if _, _, ok := SelectRescue([]RescuerFacts{forced}, patients); !ok {
		t.Fatal("provenance blocked rescue")
	}
}
