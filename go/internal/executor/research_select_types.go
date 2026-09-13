package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type ResearchSelectJournal interface {
	Journal
	PrepareResearchSelect(context.Context, domain.PlanID, domain.ActionID, store.ResearchSelectAdmission) (domain.Progress, error)
}
type ResearchSelectInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.ResearchSelectFacts
	Token                 string
}
type ResearchSelectDispatch struct {
	Attempt Placement
	Project string
	Token   string
}
type ResearchSelectEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Project               string
	Matches                domain.Fact[bool]
}

// ResearchSelectBoundary is optionally composed, like WorkBoundary: the
// routine planner has already computed the next queued project from the
// native research snapshot, so this family attaches without a hard NewWith
// constructor.
type ResearchSelectBoundary interface {
	InspectResearchSelect(context.Context, Target) (ResearchSelectInspection, error)
	SelectResearch(context.Context, ResearchSelectDispatch) (Receipt, error)
	ObserveResearchSelect(context.Context, Placement, domain.GenerationSnapshot) (ResearchSelectEvidence, error)
}

// EnableResearchSelect activates the research-select capability; see
// EnableAcquisition (in acquisition.go) for why capabilities are wired this
// way instead of inferred from a composed Boundary.
func (e *Executor) EnableResearchSelect(researchSelect ResearchSelectBoundary) error {
	if researchSelect == nil {
		return errors.New("research select boundary required")
	}
	j, ok := e.journal.(ResearchSelectJournal)
	if !ok {
		return errors.New("research select boundary requires typed journal")
	}
	e.researchSelect, e.researchSelectJournal = researchSelect, j
	return nil
}
