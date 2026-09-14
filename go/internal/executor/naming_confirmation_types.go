package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type ConfirmColonyNamesJournal interface {
	Journal
	PrepareConfirmColonyNames(context.Context, domain.PlanID, domain.ActionID, store.ConfirmColonyNamesAdmission) (domain.Progress, error)
}
type ConfirmColonyNamesInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.ConfirmColonyNamesFacts
}
type ConfirmColonyNamesDispatch struct {
	Attempt        Placement
	WindowID       int32
	FactionName    string
	SettlementName string
}
type ConfirmColonyNamesEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	WindowID              int32
	Matches               domain.Fact[bool]
}

// ConfirmColonyNamesBoundary is optionally composed, like ResearchSelectBoundary:
// the routine planner has already computed the exact observed window/
// suggestions from the native colony facts naming section, so this family
// attaches without a hard NewWith constructor.
type ConfirmColonyNamesBoundary interface {
	InspectConfirmColonyNames(context.Context, Target) (ConfirmColonyNamesInspection, error)
	ConfirmColonyNames(context.Context, ConfirmColonyNamesDispatch) (Receipt, error)
	ObserveConfirmColonyNames(context.Context, Placement, domain.GenerationSnapshot) (ConfirmColonyNamesEvidence, error)
}

// EnableConfirmColonyNames activates the naming-confirmation capability; see
// EnableResearchSelect for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableConfirmColonyNames(naming ConfirmColonyNamesBoundary) error {
	if naming == nil {
		return errors.New("naming confirmation boundary required")
	}
	j, ok := e.journal.(ConfirmColonyNamesJournal)
	if !ok {
		return errors.New("naming confirmation boundary requires typed journal")
	}
	e.naming, e.namingJournal = naming, j
	return nil
}
