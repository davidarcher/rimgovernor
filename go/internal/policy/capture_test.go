package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func capturerCandidate(id domain.PawnID) RescuerFacts {
	return RescuerFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
}
func capturePatientCandidate(id domain.PawnID, downed, prisoner bool) CapturePatientFacts {
	return CapturePatientFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(downed), Prisoner: domain.Known(prisoner), ExistingJobDef: domain.Known("")}
}

func TestSelectCaptureExcludesIneligibleCandidates(t *testing.T) {
	busy := capturerCandidate("busy")
	busy.ExistingJobDef = domain.Known("Capture")
	drafted := capturerCandidate("drafted")
	drafted.Drafted = domain.Known(true)
	fine := capturerCandidate("fine")
	standing := capturePatientCandidate("standing", false, false)
	alreadyPrisoner := capturePatientCandidate("prisoner", true, true)
	downed := capturePatientCandidate("downed", true, false)

	capturer, patient, ok := SelectCapture([]RescuerFacts{busy, drafted, fine}, []CapturePatientFacts{standing, alreadyPrisoner, downed}, "")
	if !ok || capturer != "fine" || patient != "downed" {
		t.Fatal(capturer, patient, ok)
	}
	if _, _, ok := SelectCapture([]RescuerFacts{busy, drafted}, []CapturePatientFacts{downed}, "drafted"); ok {
		t.Fatal("selected an ineligible capturer")
	}
	if _, _, ok := SelectCapture([]RescuerFacts{fine}, []CapturePatientFacts{standing, alreadyPrisoner}, ""); ok {
		t.Fatal("selected an ineligible patient")
	}
	// The warden goes first while eligible; a drafted warden yields the ID order.
	if capturer, _, ok := SelectCapture([]RescuerFacts{capturerCandidate("a"), fine}, []CapturePatientFacts{downed}, "fine"); !ok || capturer != "fine" {
		t.Fatal(capturer, ok)
	}
	if capturer, _, ok := SelectCapture([]RescuerFacts{drafted, fine, capturerCandidate("a")}, []CapturePatientFacts{downed}, "drafted"); !ok || capturer != "a" {
		t.Fatal(capturer, ok)
	}
}
