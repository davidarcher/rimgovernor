package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type DialogAnswerJournal interface {
	Journal
	PrepareDialogAnswer(context.Context, domain.PlanID, domain.ActionID, store.DialogAnswerAdmission) (domain.Progress, error)
}
type DialogAnswerInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.DialogAnswerFacts
}
type DialogAnswerDispatch struct {
	Attempt     Placement
	WindowID    int32
	OptionIndex int32
	OptionLabel string
}
type DialogAnswerEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	WindowID              int32
	Matches               domain.Fact[bool]
}

// DialogAnswerBoundary is optionally composed, like ConfirmColonyNamesBoundary:
// the routine planner has already chosen the exact observed window/option
// from the native colony facts dialog section (#156), so this family
// attaches without a hard NewWith constructor.
type DialogAnswerBoundary interface {
	InspectDialogAnswer(context.Context, Target) (DialogAnswerInspection, error)
	AnswerDialog(context.Context, DialogAnswerDispatch) (Receipt, error)
	ObserveDialogAnswer(context.Context, Placement, domain.GenerationSnapshot) (DialogAnswerEvidence, error)
}

// EnableDialogAnswer activates the dialog-answer capability; see
// EnableResearchSelect for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableDialogAnswer(dialog DialogAnswerBoundary) error {
	if dialog == nil {
		return errors.New("dialog answer boundary required")
	}
	j, ok := e.journal.(DialogAnswerJournal)
	if !ok {
		return errors.New("dialog answer boundary requires typed journal")
	}
	e.dialog, e.dialogJournal = dialog, j
	return nil
}
