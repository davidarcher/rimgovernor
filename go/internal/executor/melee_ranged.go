package executor

import "errors"

// NewWithMeleeAndRanged composes both attack boundaries onto one draft-backed
// executor: a single squad-defense plan can assign both SquadMelee and
// SquadRanged engagements, so live defense dispatch needs both boundaries
// wired together, not as mutually exclusive alternatives.
func NewWithMeleeAndRanged(journal interface {
	MeleeJournal
	RangedJournal
}, building Boundary, draft DraftBoundary, melee MeleeBoundary, ranged RangedBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if ranged == nil {
		return nil, errors.New("ranged boundary required")
	}
	e, err := NewWithMelee(journal, building, draft, melee, clock, limits, routine...)
	if err != nil {
		return nil, err
	}
	e.rangedJournal, e.ranged = journal, ranged
	return e, nil
}
