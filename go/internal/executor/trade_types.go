package executor

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TradeJournal is Journal plus the untyped Prepare: a trade intent names
// its trader and negotiator itself and native re-validates it against the
// one live session, so there is no trade admission row to persist.
type TradeJournal interface {
	Journal
	Prepare(context.Context, domain.PlanID, domain.ActionID, domain.GenerationSnapshot, domain.Tick) (domain.Progress, error)
}

type TradeInspection struct {
	StartedAt, ObservedAt time.Time
	Facts                 policy.TradeAdmissionFacts
}

// TradeBoundary is optionally composed: the routine upstream has already
// selected the trader/negotiator/lines/floors, so this family attaches
// without a hard NewWith constructor.
type TradeBoundary interface {
	InspectTrade(context.Context, Target) (TradeInspection, error)
	WriteTrade(context.Context, Placement) (Receipt, error)
}

// EnableTrade activates the trade capability; capabilities are wired this way
// instead of inferred from a composed Boundary.
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
