package executor

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// resolveTradeDependency finds which Open action a set_lines/accept/end
// action's session belongs to, then looks up the session that Open recorded.
// TradeOpen actions carry no dependency and resolve to the zero
// TradeDependency. This never re-derives or guesses which session is open: it
// is either the one durably recorded against the exact Open action id, or
// unresolved.
//
// Two bindings are honoured, in this order:
//
//  1. The plan's own domain.ActionDependency, declared by whoever built a
//     multi-action trade plan. Unchanged, and still the only binding such a
//     plan needs.
//  2. Failing that, a durable cross-plan store.RecordTradeSessionReference
//     bound to this action's own identity at submission time. This is what
//     lets the multi-phase negotiation driver submit each phase as its own
//     one-action plan, which it must: SetTradeLines' concrete native line ids
//     and counts only exist after Open has actually executed, and a committed
//     PlanSpec is immutable. See store/trade_session_reference.go.
//
// Either way resolution ends in the same unchanged, never plan-scoped
// LookupTradeSession, so an Open not yet observed complete still resolves to
// unresolved.
func (e *Executor) resolveTradeDependency(ctx context.Context, plan domain.PlanID, action domain.ActionID, kind domain.TradeOperationKind) (TradeDependency, error) {
	if kind == domain.TradeOpen {
		return TradeDependency{}, nil
	}
	state, err := e.journal.LoadPlan(ctx, plan)
	if err != nil {
		return TradeDependency{}, err
	}
	var openAction domain.ActionID
	found := false
	for _, d := range state.Spec.Dependencies() {
		if d.Action == action {
			openAction, found = d.Requires, true
			break
		}
	}
	if !found {
		if openAction, found, err = e.tradeJournal.LookupTradeSessionReference(ctx, action); err != nil {
			return TradeDependency{}, err
		}
	}
	if !found {
		return TradeDependency{}, nil
	}
	session, resolved, err := e.tradeJournal.LookupTradeSession(ctx, openAction)
	if err != nil {
		return TradeDependency{}, err
	}
	return TradeDependency{OpenAction: openAction, Session: session, Resolved: resolved}, nil
}

func tradeAdmission(expected domain.GenerationSnapshot, kind domain.TradeOperationKind, tick domain.Tick, facts policy.TradeAdmissionFacts) store.TradeAdmission {
	admission := store.TradeAdmission{Snapshot: expected, Tick: tick, Kind: kind}
	if kind == domain.TradeOpen {
		admission.TraderSnapshotToken, admission.NegotiatorSnapshotToken = facts.Open.TraderSnapshotToken, facts.Open.NegotiatorSnapshotToken
	} else {
		admission.SessionToken = facts.Session.SessionToken
	}
	return admission
}

func (e *Executor) runTrade(ctx context.Context, action domain.Action, p domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: p}
	trade, ok := action.Trade()
	if !ok || p.Action() != action {
		return result, ErrEvidence
	}
	v := p.View()
	if v.Unresolved {
		return e.reconcileTrade(ctx, result, generation)
	}
	switch v.Stage {
	case domain.Completed, domain.Cancelled, domain.Unsuccessful:
		return result, nil
	case domain.Pending, domain.Prepared:
	default:
		return result, ErrEvidence
	}
	expected := authority.Snapshot
	if expected.Plan != v.Plan || expected.Revision != v.Revision {
		if e.routineScope == nil {
			return result, ErrAuthority
		}
		expected.Plan, expected.Revision = v.Plan, v.Revision
	}
	dependency, err := e.resolveTradeDependency(ctx, v.Plan, v.Action, trade.Kind())
	if err != nil {
		return result, err
	}
	minimum := v.Tick
	var inspection TradeInspection
	for range 2 {
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		inspection, err = e.trade.InspectTrade(ctx, Target{action, expected}, dependency)
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if inspection.Facts.Snapshot != expected || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return result, ErrHeld
		}
		facts := inspection.Facts
		minimum = max(minimum, facts.PreviewTick)
		decision := policy.EvaluateTrade(policy.TradeRequest{Action: action, Progress: result.Progress, Current: expected, MinimumTick: minimum, Facts: facts})
		result.Refused = decision.Refused
		if !decision.Admitted {
			result.Progress = e.holdRefusal(ctx, v.Plan, v.Action, decision.Refused, minimum, result.Progress)
			return result, ErrHeld
		}
		admission := tradeAdmission(expected, trade.Kind(), facts.PreviewTick, facts)
		next, err := e.tradeJournal.PrepareTrade(ctx, v.Plan, v.Action, admission)
		if err != nil {
			return result, err
		}
		result.Progress = next
		minimum = max(minimum, facts.PreviewTick)
	}
	if err = e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, inspection.Facts.PreviewTick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, inspection.Facts.PreviewTick}
	admission := tradeAdmission(expected, trade.Kind(), inspection.Facts.PreviewTick, inspection.Facts)
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.record(result, v.Plan, attempt, domain.ReceiptUnknown, ErrHeld)
	}
	result.NativeCalled = true
	receipt, err := e.trade.WriteTrade(ctx, TradeDispatch{attempt, admission, dependency})
	kind := receipt.Kind
	if err != nil {
		kind = domain.ReceiptUnknown
	} else if receipt.Action != v.Action || receipt.Attempt != attempt.Attempt || receipt.Snapshot != expected {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	if _, check := next.RecordReceipt(attempt.Attempt, kind); check != nil {
		kind, err = domain.ReceiptUnknown, errors.Join(err, ErrEvidence)
	}
	return e.record(result, v.Plan, attempt, kind, errors.Join(err, ctx.Err()))
}

func (e *Executor) reconcileTrade(ctx context.Context, result Result, generation context.Context) (Result, error) {
	p := result.Progress
	v := p.View()
	current := e.current().Snapshot
	if current.Validate() != nil || current.Native == 0 || current.Native < v.Snapshot.Native || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
		return result, ErrAuthority
	}
	state, err := e.journal.LoadPlan(ctx, v.Plan)
	if err != nil {
		return result, err
	}
	trade, _ := p.Action().Trade()
	var admission store.TradeAdmission
	found := false
	for _, record := range state.TradeAdmissions {
		if record.Action == v.Action {
			admission, found = record.Admission, true
		}
	}
	if !found || admission.Snapshot != v.Snapshot || admission.Tick > v.Tick {
		return result, ErrEvidence
	}
	dependency, err := e.resolveTradeDependency(ctx, v.Plan, v.Action, trade.Kind())
	if err != nil {
		return result, err
	}
	evidence, err := e.trade.ObserveTrade(ctx, TradeDispatch{Placement{p.Action(), v.Attempt, v.Snapshot, v.Tick}, admission, dependency}, current)
	if err != nil {
		return result, err
	}
	if ctx.Err() != nil {
		return result, ctx.Err()
	}
	if generation.Err() != nil || e.current().Snapshot != current {
		return result, ErrAuthority
	}
	o := evidence.Observation
	if o.Action != v.Action || o.Attempt != v.Attempt || o.Snapshot != current || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
		return result, ErrEvidence
	}
	switch o.Effect {
	case domain.EffectCompleted, domain.EffectAbsent, domain.EffectUnsuccessful:
		if !evidence.Complete || o.Causality != domain.AfterDispatch {
			return result, ErrEvidence
		}
	case domain.EffectUnknown, domain.EffectPending:
	default:
		return result, ErrEvidence
	}
	// Trade is the single vertical whose completion evidence produces a
	// native-assigned identifier (the session id/token) a later same-plan
	// action must consume; persist it durably here, the one place Open's
	// completion is first confirmed. Best-effort against a store failure
	// here would still leave the observation itself unrecorded below, so a
	// failure here simply surfaces as an error and the caller retries.
	if trade.Kind() == domain.TradeOpen && o.Effect == domain.EffectCompleted && evidence.SessionID != "" && evidence.SessionToken != "" {
		if err = e.tradeJournal.RecordTradeSession(ctx, v.Action, store.TradeSession{SessionID: evidence.SessionID, SessionToken: evidence.SessionToken}); err != nil {
			return result, err
		}
	}
	next, err := e.journal.Observe(ctx, v.Plan, o, current)
	if err == nil {
		result.Progress = next
	}
	return result, err
}
