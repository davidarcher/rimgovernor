package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TradeAdmissionFacts anchors one trade intent's dispatch to a fresh native
// read (the trade-session read's context). Trade is an intent-mode kind:
// native judges the intent against live state when it applies and refuses
// it with a terminal receipt, so admission never gates on a native verdict.
type TradeAdmissionFacts struct {
	Snapshot    domain.GenerationSnapshot
	PreviewTick domain.Tick
}

type TradeRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       TradeAdmissionFacts
}

// EvaluateTrade re-validates one already-selected trade intent immediately
// before dispatch: the canonical action, its progress and a fresh anchor in
// the current world. It never selects a trader, lines or floors.
func EvaluateTrade(r TradeRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	trade, ok := r.Action.Trade()
	canonical, err := domain.NewTradeAction(r.Action.ID(), trade)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map)) {
		return refuse(StaleFacts)
	}
	return DraftDecision{Admitted: true}
}
