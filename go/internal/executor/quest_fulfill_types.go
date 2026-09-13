package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type QuestFulfillJournal interface {
	Journal
	PrepareQuestFulfill(context.Context, domain.PlanID, domain.ActionID, store.QuestFulfillAdmission) (domain.Progress, error)
}

type QuestFulfillInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.QuestFulfillAdmissionFacts
}

type QuestFulfillDispatch struct {
	Attempt   Placement
	Admission store.QuestFulfillAdmission
}

type QuestFulfillEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Quest                 domain.QuestID
}

// QuestFulfillBoundary is optionally composed, like SettlementGiftBoundary:
// the planner upstream has already selected the quest/caravan pair, so this
// family attaches without a hard NewWith constructor.
type QuestFulfillBoundary interface {
	InspectQuestFulfill(context.Context, Target) (QuestFulfillInspection, error)
	WriteQuestFulfill(context.Context, QuestFulfillDispatch) (Receipt, error)
	ObserveQuestFulfill(context.Context, QuestFulfillDispatch, domain.GenerationSnapshot) (QuestFulfillEvidence, error)
}

// EnableQuestFulfill activates the quest-fulfill capability; see
// EnableQuestAccept for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableQuestFulfill(questFulfill QuestFulfillBoundary) error {
	if questFulfill == nil {
		return errors.New("quest fulfill boundary required")
	}
	j, ok := e.journal.(QuestFulfillJournal)
	if !ok {
		return errors.New("quest fulfill boundary requires typed journal")
	}
	e.questFulfill, e.questFulfillJournal = questFulfill, j
	return nil
}
