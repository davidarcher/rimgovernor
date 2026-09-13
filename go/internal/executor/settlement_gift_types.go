package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type SettlementGiftJournal interface {
	Journal
	PrepareSettlementGift(context.Context, domain.PlanID, domain.ActionID, store.SettlementGiftAdmission) (domain.Progress, error)
}

type SettlementGiftInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.SettlementGiftAdmissionFacts
}

type SettlementGiftDispatch struct {
	Attempt   Placement
	Admission store.SettlementGiftAdmission
}

type SettlementGiftEvidence struct {
	Observation           domain.Observation
	StartedAt, ObservedAt time.Time
	Complete              bool
	Caravan               domain.CaravanID
}

// SettlementGiftBoundary is optionally composed, like QuestAcceptBoundary:
// the planner upstream has already selected the caravan/settlement/faction/
// silver tuple, so this family attaches without a hard NewWith constructor.
type SettlementGiftBoundary interface {
	InspectSettlementGift(context.Context, Target) (SettlementGiftInspection, error)
	WriteSettlementGift(context.Context, SettlementGiftDispatch) (Receipt, error)
	ObserveSettlementGift(context.Context, SettlementGiftDispatch, domain.GenerationSnapshot) (SettlementGiftEvidence, error)
}

// EnableSettlementGift activates the settlement-gift capability; see
// EnableQuestAccept for why capabilities are wired this way instead of
// inferred from a composed Boundary.
func (e *Executor) EnableSettlementGift(settlementGift SettlementGiftBoundary) error {
	if settlementGift == nil {
		return errors.New("settlement gift boundary required")
	}
	j, ok := e.journal.(SettlementGiftJournal)
	if !ok {
		return errors.New("settlement gift boundary requires typed journal")
	}
	e.settlementGift, e.settlementGiftJournal = settlementGift, j
	return nil
}
