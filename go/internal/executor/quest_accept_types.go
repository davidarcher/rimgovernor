package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type QuestAcceptJournal interface {
	Journal
	PrepareQuestAccept(context.Context, domain.PlanID, domain.ActionID, store.QuestAcceptAdmission) (domain.Progress, error)
}

type QuestAcceptInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.QuestAcceptFacts
}

type QuestAcceptDispatch struct {
	Attempt   Placement
	Admission store.QuestAcceptAdmission
}

type QuestAcceptEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Quest                 domain.QuestID
}

// QuestAcceptBoundary is optionally composed, like PrisonerInteractionBoundary:
// the planner upstream has already selected the quest/accepter/reward-choice
// tuple, so this family attaches without a hard NewWith constructor.
type QuestAcceptBoundary interface {
	InspectQuestAccept(context.Context, Target) (QuestAcceptInspection, error)
	WriteQuestAccept(context.Context, QuestAcceptDispatch) (Receipt, error)
	ObserveQuestAccept(context.Context, QuestAcceptDispatch, domain.GenerationSnapshot) (QuestAcceptEvidence, error)
}

// EnableQuestAccept activates the quest-accept capability; see
// EnablePrisonerInteraction for why capabilities are wired this way instead
// of inferred from a composed Boundary.
func (e *Executor) EnableQuestAccept(questAccept QuestAcceptBoundary) error {
	if questAccept == nil {
		return errors.New("quest accept boundary required")
	}
	j, ok := e.journal.(QuestAcceptJournal)
	if !ok {
		return errors.New("quest accept boundary requires typed journal")
	}
	e.questAccept, e.questAcceptJournal = questAccept, j
	return nil
}
