package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type PrisonerInteractionJournal interface {
	Journal
	PreparePrisonerInteraction(context.Context, domain.PlanID, domain.ActionID, store.PrisonerInteractionAdmission) (domain.Progress, error)
}

type PrisonerInteractionInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.PrisonerInteractionFacts
}

type PrisonerInteractionDispatch struct {
	Attempt   Placement
	Admission store.PrisonerInteractionAdmission
}

type PrisonerInteractionEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Pawn                  domain.PawnID
}

// PrisonerInteractionBoundary is optionally composed, like HusbandryBoundary:
// no pawn is drafted, and the planner has already selected the
// prisoner/interaction pair, so this family attaches without a hard NewWith
// constructor.
type PrisonerInteractionBoundary interface {
	InspectPrisonerInteraction(context.Context, Target) (PrisonerInteractionInspection, error)
	WritePrisonerInteraction(context.Context, PrisonerInteractionDispatch) (Receipt, error)
	ObservePrisonerInteraction(context.Context, PrisonerInteractionDispatch, domain.GenerationSnapshot) (PrisonerInteractionEvidence, error)
}

// EnablePrisonerInteraction activates the prisoner interaction capability;
// see EnableHusbandry for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnablePrisonerInteraction(prisonerInteraction PrisonerInteractionBoundary) error {
	if prisonerInteraction == nil {
		return errors.New("prisoner interaction boundary required")
	}
	j, ok := e.journal.(PrisonerInteractionJournal)
	if !ok {
		return errors.New("prisoner interaction boundary requires typed journal")
	}
	e.prisonerInteraction, e.prisonerInteractionJournal = prisonerInteraction, j
	return nil
}
