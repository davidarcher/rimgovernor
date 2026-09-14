package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type MoodReliefJournal interface {
	Journal
	PrepareMoodRelief(context.Context, domain.PlanID, domain.ActionID, store.MoodReliefAdmission) (domain.Progress, error)
}

type MoodReliefInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.MoodReliefDispatchFacts
}

type MoodReliefDispatch struct {
	Attempt   Placement
	Admission store.MoodReliefAdmission
}

type MoodReliefEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
}

// MoodReliefBoundary is optionally composed, like WasteBoundary: the pawn
// is not drafted, so this family attaches without a hard NewWithMoodRelief
// ctor.
type MoodReliefBoundary interface {
	InspectMoodRelief(context.Context, Target) (MoodReliefInspection, error)
	ManageMoodRelief(context.Context, MoodReliefDispatch) (Receipt, error)
	ObserveMoodRelief(context.Context, MoodReliefDispatch, domain.GenerationSnapshot) (MoodReliefEvidence, error)
}

// EnableMoodRelief activates the mood relief capability; see EnableWaste
// (in waste_types.go) for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableMoodRelief(moodRelief MoodReliefBoundary) error {
	if moodRelief == nil {
		return errors.New("mood relief boundary required")
	}
	j, ok := e.journal.(MoodReliefJournal)
	if !ok {
		return errors.New("mood relief boundary requires typed journal")
	}
	e.moodRelief, e.moodReliefJournal = moodRelief, j
	return nil
}
