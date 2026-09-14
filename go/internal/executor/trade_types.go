package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// TradeJournal extends Journal with Trade's own admission and session
// persistence. RecordTradeSession/LookupTradeSession are the durable store
// this vertical adds: see store.TradeSession's doc comment for why no
// existing mechanism captures a native-assigned session id for a later
// same-plan action to consume.
type TradeJournal interface {
	Journal
	PrepareTrade(context.Context, domain.PlanID, domain.ActionID, store.TradeAdmission) (domain.Progress, error)
	RecordTradeSession(context.Context, domain.ActionID, store.TradeSession) error
	LookupTradeSession(context.Context, domain.ActionID) (store.TradeSession, bool, error)
}

// TradeDependency carries the resolved identity of a set_lines/accept/end
// action's dependency Open action, and (once recorded) its persisted
// session. The executor resolves this once per attempt from the plan's own
// domain.ActionDependency plus the journal's session record, never by
// re-deriving or guessing which session is open. It is the zero value for
// TradeOpen actions, which carry no dependency.
type TradeDependency struct {
	OpenAction domain.ActionID
	Session    store.TradeSession
	Resolved   bool
}

type TradeInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.TradeAdmissionFacts
}

type TradeDispatch struct {
	Attempt    Placement
	Admission  store.TradeAdmission
	Dependency TradeDependency
}

// TradeEvidence's SessionID/SessionToken are populated only when Observe
// reports a completed TradeOpen: that is the one and only evidence carrying
// the native-assigned session identity, which the executor then persists via
// TradeJournal.RecordTradeSession for later same-plan actions to consume.
type TradeEvidence struct {
	Observation             domain.Observation
	StartedAt, ObservedAt   time.Time
	Complete                bool
	SessionID, SessionToken string
}

// TradeBoundary is optionally composed, like SettlementGiftBoundary/
// QuestFulfillBoundary: the planner (or direct player command) upstream has
// already selected the trader/negotiator/lines/floors, so this family
// attaches without a hard NewWith constructor.
type TradeBoundary interface {
	InspectTrade(context.Context, Target, TradeDependency) (TradeInspection, error)
	WriteTrade(context.Context, TradeDispatch) (Receipt, error)
	ObserveTrade(context.Context, TradeDispatch, domain.GenerationSnapshot) (TradeEvidence, error)
}

// EnableTrade activates the trade capability; see EnableQuestFulfill for why
// capabilities are wired this way instead of inferred from a composed
// Boundary.
func (e *Executor) EnableTrade(trade TradeBoundary) error {
	if trade == nil {
		return errors.New("trade boundary required")
	}
	j, ok := e.journal.(TradeJournal)
	if !ok {
		return errors.New("trade boundary requires typed journal")
	}
	e.trade, e.tradeJournal = trade, j
	return nil
}
