package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// NoOpReason says why a goal detector raised nothing in a review: a closed
// vocabulary, so a silent no-op is never read as "satisfied" (#1909).
type NoOpReason string

const (
	// NoOpInputsUnknown: the facts the detector needs were not measured, so
	// the need is unknown, not recovered.
	NoOpInputsUnknown NoOpReason = "inputs_unknown"
	// NoOpNotApplicable: the detector filed no assessment (its precondition
	// does not hold: no such need, no such pawn, no disaster history).
	NoOpNotApplicable NoOpReason = "not_applicable"
	// NoOpSatisfied: the facts are measured and the need is met.
	NoOpSatisfied NoOpReason = "satisfied"
)

// Validate refuses a reason outside the vocabulary.
func (r NoOpReason) Validate() error {
	switch r {
	case NoOpInputsUnknown, NoOpNotApplicable, NoOpSatisfied:
		return nil
	}
	return fmt.Errorf("unknown no-op reason %q", string(r))
}

// NoOpRecord is one detector's recorded no-op in the review.
type NoOpRecord struct {
	Goal   GoalID
	Reason NoOpReason
}

// noOpOf classifies what a detector left on the run: it is a no-op when it
// raised no goal and filed no deficit assessment for its goal. The reason
// follows the assessments it filed: none is not_applicable, any unknown is
// inputs_unknown, otherwise satisfied.
func noOpOf(id GoalID, goals []DevelopmentGoal, assessments []RoutineAssessment) (NoOpRecord, bool) {
	reason := NoOpNotApplicable
	for _, g := range goals {
		if g.ID == id {
			return NoOpRecord{}, false
		}
	}
	for _, a := range assessments {
		if a.ID != id {
			continue
		}
		switch a.Need {
		case domain.NeedDeficit:
			return NoOpRecord{}, false
		case domain.NeedUnknown:
			reason = NoOpInputsUnknown
		default:
			if reason != NoOpInputsUnknown {
				reason = NoOpSatisfied
			}
		}
	}
	return NoOpRecord{Goal: id, Reason: reason}, true
}
