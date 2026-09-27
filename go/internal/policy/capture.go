package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SelectCapture pairs the warden (WardenFor over the roster, "" when none
// qualifies) when it is an available undrafted capturer, otherwise the
// first such capturer by ID, with the first downed, not-yet-captured
// patient (by ID for a stable, deterministic choice), mirroring
// SelectRescue. This is a proposal only; EvaluateCapture re-validates the
// chosen pair.
func SelectCapture(capturers []RescuerFacts, patients []CapturePatientFacts, warden domain.PawnID) (domain.PawnID, domain.PawnID, bool) {
	eligibleCapturer := func(r RescuerFacts) bool {
		dead, dk := r.Dead.Value()
		downed, wk := r.Downed.Value()
		drafted, tk := r.Drafted.Value()
		mental, mk := r.MentalState.Value()
		forced, fk := r.PlayerForced.Value()
		queued, qk := r.QueuedJobs.Value()
		existing, ek := r.ExistingJobDef.Value()
		if !dk || !wk || !tk || !mk || !fk || !qk || !ek {
			return false
		}
		return !dead && !downed && !drafted && !mental && !forced && queued == 0 && existing != "Capture"
	}
	eligiblePatient := func(p CapturePatientFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		prisoner, pk := p.Prisoner.Value()
		existing, ek := p.ExistingJobDef.Value()
		if !dk || !wk || !pk || !ek {
			return false
		}
		return !dead && downed && !prisoner && existing != "Capture"
	}
	var capturerPool []RescuerFacts
	for _, r := range capturers {
		if eligibleCapturer(r) {
			capturerPool = append(capturerPool, r)
		}
	}
	var patientPool []CapturePatientFacts
	for _, p := range patients {
		if eligiblePatient(p) {
			patientPool = append(patientPool, p)
		}
	}
	if len(capturerPool) == 0 || len(patientPool) == 0 {
		return "", "", false
	}
	sort.Slice(capturerPool, func(i, j int) bool {
		if a, b := capturerPool[i].Pawn == warden, capturerPool[j].Pawn == warden; a != b {
			return a
		}
		return capturerPool[i].Pawn < capturerPool[j].Pawn
	})
	sort.Slice(patientPool, func(i, j int) bool { return patientPool[i].Pawn < patientPool[j].Pawn })
	return capturerPool[0].Pawn, patientPool[0].Pawn, true
}

// CapturePatientFacts describes one candidate capture target. Completion is
// Prisoner true (the native Capture job carries the pawn into a prisoner
// bed and marks it a prisoner); a downed pawn who has simply stopped moving
// is not evidence of a completed capture. Capturer eligibility is identical
// to Rescue's RescuerFacts (an undrafted, capable performer), so it is
// reused rather than duplicated.
type CapturePatientFacts struct {
	Pawn                   domain.PawnID
	SnapshotToken          string
	Dead, Downed, Prisoner domain.Fact[bool]
	ExistingJobDef         domain.Fact[string]
}
