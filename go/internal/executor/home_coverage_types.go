package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type HomeCoverageJournal interface {
	Journal
	PrepareHomeCoverage(context.Context, domain.PlanID, domain.ActionID, store.HomeCoverageAdmission) (domain.Progress, error)
}

type HomeCoverageInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.HomeCoverageFacts
}

type HomeCoverageDispatch struct {
	Attempt   Placement
	Admission store.HomeCoverageAdmission
}

type HomeCoverageEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Target                string
}

// HomeCoverageBoundary is optionally composed, like BedAssignBoundary: no
// pawn is drafted or moved, and the planner has already selected the
// target/shape pair from a fresh ReviewHomeCoverage selection, so this
// family attaches without a hard NewWith constructor.
type HomeCoverageBoundary interface {
	InspectHomeCoverage(context.Context, Target) (HomeCoverageInspection, error)
	ExtendHomeCoverage(context.Context, HomeCoverageDispatch) (Receipt, error)
	ObserveHomeCoverage(context.Context, HomeCoverageDispatch, domain.GenerationSnapshot) (HomeCoverageEvidence, error)
}

// EnableHomeCoverage activates the home-coverage capability; see
// EnableBedAssign for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableHomeCoverage(homeCoverage HomeCoverageBoundary) error {
	if homeCoverage == nil {
		return errors.New("home coverage boundary required")
	}
	j, ok := e.journal.(HomeCoverageJournal)
	if !ok {
		return errors.New("home coverage boundary requires typed journal")
	}
	e.homeCoverage, e.homeCoverageJournal = homeCoverage, j
	return nil
}
