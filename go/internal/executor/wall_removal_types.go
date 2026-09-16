package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type WallRemovalJournal interface {
	Journal
	PrepareWallRemoval(context.Context, domain.PlanID, domain.ActionID, store.WallRemovalAdmission) (domain.Progress, error)
}

type WallRemovalInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.WallRemovalFacts
}

type WallRemovalDispatch struct {
	Attempt   Placement
	Admission store.WallRemovalAdmission
}

// WallRemovalEvidence's Retired is the reconcile outcome: native
// cancelled this pending demolition (player interference, a safety change)
// rather than completing or leaving it pending. The executor cancels this
// step's not-yet-dispatched same-plan dependents when it sees Retired, since
// dependency completion alone never proves the object those dependents
// expect will still arrive.
type WallRemovalEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Retired               bool
	Target                string
}

// WallRemovalBoundary is optionally composed, like HomeCoverageBoundary: the
// planner has already selected the original/backup identity and site
// geometry from a fresh ReviewStoneShell selection, so this family attaches
// without a hard NewWith constructor.
type WallRemovalBoundary interface {
	InspectWallRemoval(context.Context, Target) (WallRemovalInspection, error)
	ExecuteWallRemoval(context.Context, WallRemovalDispatch) (Receipt, error)
	ObserveWallRemoval(context.Context, WallRemovalDispatch, domain.GenerationSnapshot) (WallRemovalEvidence, error)
}

// EnableWallRemoval activates the wall-removal capability; see
// EnableHomeCoverage for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableWallRemoval(wallRemoval WallRemovalBoundary) error {
	if wallRemoval == nil {
		return errors.New("wall removal boundary required")
	}
	j, ok := e.journal.(WallRemovalJournal)
	if !ok {
		return errors.New("wall removal boundary requires typed journal")
	}
	e.wallRemoval, e.wallRemovalJournal = wallRemoval, j
	return nil
}
