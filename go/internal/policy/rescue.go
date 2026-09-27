package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RescuerFacts describes one undrafted candidate rescuer.
type RescuerFacts struct {
	Pawn                      domain.PawnID
	SnapshotToken             string
	Dead, Downed, Drafted     domain.Fact[bool]
	MentalState, PlayerForced domain.Fact[bool]
	QueuedJobs                domain.Fact[uint32]
	ExistingJobDef            domain.Fact[string]
}

// RescuePatientFacts describes one candidate patient. Completion is InBed with
// a known non-empty BedID; a downed pawn who has simply stopped moving is not
// evidence of a completed rescue.
type RescuePatientFacts struct {
	Pawn                domain.PawnID
	SnapshotToken       string
	Dead, Downed, InBed domain.Fact[bool]
	BedID               domain.Fact[string]
	ExistingJobDef      domain.Fact[string]
}

// SelectRescue pairs the first available undrafted rescuer with the first
// downed, not-yet-bedded patient (by ID for a stable, deterministic choice).
// This is a proposal only; EvaluateRescue re-validates the chosen pair.
func SelectRescue(rescuers []RescuerFacts, patients []RescuePatientFacts) (domain.PawnID, domain.PawnID, bool) {
	eligibleRescuer := func(r RescuerFacts) bool {
		dead, dk := r.Dead.Value()
		downed, wk := r.Downed.Value()
		drafted, tk := r.Drafted.Value()
		mental, mk := r.MentalState.Value()
		existing, ek := r.ExistingJobDef.Value()
		if !dk || !wk || !tk || !mk || !ek {
			return false
		}
		return !dead && !downed && !drafted && !mental && existing != "Rescue"
	}
	eligiblePatient := func(p RescuePatientFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		inBed, bk := p.InBed.Value()
		existing, ek := p.ExistingJobDef.Value()
		if !dk || !wk || !bk || !ek {
			return false
		}
		return !dead && downed && !inBed && existing != "Rescue"
	}
	var rescuerPool []RescuerFacts
	for _, r := range rescuers {
		if eligibleRescuer(r) {
			rescuerPool = append(rescuerPool, r)
		}
	}
	var patientPool []RescuePatientFacts
	for _, p := range patients {
		if eligiblePatient(p) {
			patientPool = append(patientPool, p)
		}
	}
	if len(rescuerPool) == 0 || len(patientPool) == 0 {
		return "", "", false
	}
	sort.Slice(rescuerPool, func(i, j int) bool {
		a, b := orderedWorkCost(rescuerPool[i].PlayerForced, rescuerPool[i].QueuedJobs), orderedWorkCost(rescuerPool[j].PlayerForced, rescuerPool[j].QueuedJobs)
		if a != b {
			return a < b
		}
		return rescuerPool[i].Pawn < rescuerPool[j].Pawn
	})
	sort.Slice(patientPool, func(i, j int) bool { return patientPool[i].Pawn < patientPool[j].Pawn })
	return rescuerPool[0].Pawn, patientPool[0].Pawn, true
}
