package executor

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// inertTradeBoundary satisfies EnableTrade without doing anything: these tests
// exercise resolveTradeDependency itself against a real store, not a dispatch.
type inertTradeBoundary struct{}

func (inertTradeBoundary) InspectTrade(context.Context, Target, TradeDependency) (TradeInspection, error) {
	return TradeInspection{}, errors.New("not dispatched in this test")
}
func (inertTradeBoundary) WriteTrade(context.Context, TradeDispatch) (Receipt, error) {
	return Receipt{}, errors.New("not dispatched in this test")
}
func (inertTradeBoundary) ObserveTrade(context.Context, TradeDispatch, domain.GenerationSnapshot) (TradeEvidence, error) {
	return TradeEvidence{}, errors.New("not dispatched in this test")
}

func tradeOpenAction(t *testing.T, id domain.ActionID) domain.Action {
	t.Helper()
	open, err := domain.NewTradeOpen("settlement-1", "negotiator-1", false)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewTradeAction(id, open)
	if err != nil {
		t.Fatal(err)
	}
	return action
}

func tradeLinesAction(t *testing.T, id domain.ActionID) domain.Action {
	t.Helper()
	lines, err := domain.NewTradeSetLines([]domain.TradeLine{{LineID: "line-1", AbsoluteCount: 5}}, false)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewTradeAction(id, lines)
	if err != nil {
		t.Fatal(err)
	}
	return action
}

func tradePlan(t *testing.T, journal *store.Store, id domain.PlanID, actions []domain.Action, dependencies ...domain.ActionDependency) {
	t.Helper()
	plan, err := domain.NewPlan(id, 1, actions, dependencies...)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
}

func tradeResolutionFixture(t *testing.T) *fixture {
	t.Helper()
	f := newFixture(t)
	if err := f.executor.EnableTrade(inertTradeBoundary{}); err != nil {
		t.Fatal(err)
	}
	return f
}

// TestResolveTradeDependencyAcrossPlans is the cross-plan binding's own test:
// a set_lines action committed in its own later plan, with no same-plan
// dependency edge available to it at all, still resolves the session its
// negotiation's Open action produced -- and only once that Open has actually
// been observed complete.
func TestResolveTradeDependencyAcrossPlans(t *testing.T) {
	f := tradeResolutionFixture(t)
	ctx := context.Background()
	tradePlan(t, f.store, "trade-open-plan", []domain.Action{tradeOpenAction(t, "open-action-1")})
	tradePlan(t, f.store, "trade-lines-plan", []domain.Action{tradeLinesAction(t, "lines-action-1")})

	// Unbound: no reference, no guess.
	got, err := f.executor.resolveTradeDependency(ctx, "trade-lines-plan", "lines-action-1", domain.TradeSetLines)
	if err != nil || got.OpenAction != "" || got.Resolved {
		t.Fatalf("got %+v err %v, want an unresolved zero dependency before any binding", got, err)
	}

	// Bound, but the Open has not completed: the open action is known and the
	// session is still unresolved, so admission refuses rather than guessing.
	if err = f.store.RecordTradeSessionReference(ctx, "lines-action-1", "open-action-1"); err != nil {
		t.Fatal(err)
	}
	if got, err = f.executor.resolveTradeDependency(ctx, "trade-lines-plan", "lines-action-1", domain.TradeSetLines); err != nil {
		t.Fatal(err)
	}
	if got.OpenAction != "open-action-1" || got.Resolved || got.Session != (store.TradeSession{}) {
		t.Fatalf("got %+v, want open-action-1 with no session yet", got)
	}

	// Open completed and recorded its native session: now it resolves whole.
	session := store.TradeSession{SessionID: "session-1", SessionToken: "session-token-1"}
	if err = f.store.RecordTradeSession(ctx, "open-action-1", session); err != nil {
		t.Fatal(err)
	}
	if got, err = f.executor.resolveTradeDependency(ctx, "trade-lines-plan", "lines-action-1", domain.TradeSetLines); err != nil {
		t.Fatal(err)
	}
	if !got.Resolved || got.OpenAction != "open-action-1" || got.Session != session {
		t.Fatalf("got %+v, want the recorded session resolved through the cross-plan reference", got)
	}
}

// TestResolveTradeDependencyPrefersPlanDependency guards the unchanged
// original path: a plan that declares its own dependency edge resolves through
// that edge, and a reference is never consulted for it.
func TestResolveTradeDependencyPrefersPlanDependency(t *testing.T) {
	f := tradeResolutionFixture(t)
	ctx := context.Background()
	tradePlan(t, f.store, "trade-plan", []domain.Action{
		tradeOpenAction(t, "same-plan-open"), tradeLinesAction(t, "same-plan-lines"),
	}, domain.ActionDependency{Action: "same-plan-lines", Requires: "same-plan-open"})
	tradePlan(t, f.store, "other-plan", []domain.Action{tradeOpenAction(t, "other-open")})

	// A conflicting reference exists for the very same action; the plan's own
	// declared edge must still win, so a later feature can never silently
	// re-point an existing multi-action trade plan at another session.
	if err := f.store.RecordTradeSessionReference(ctx, "same-plan-lines", "other-open"); err != nil {
		t.Fatal(err)
	}
	mine := store.TradeSession{SessionID: "mine", SessionToken: "mine-token"}
	if err := f.store.RecordTradeSession(ctx, "same-plan-open", mine); err != nil {
		t.Fatal(err)
	}
	if err := f.store.RecordTradeSession(ctx, "other-open", store.TradeSession{SessionID: "theirs", SessionToken: "theirs-token"}); err != nil {
		t.Fatal(err)
	}
	got, err := f.executor.resolveTradeDependency(ctx, "trade-plan", "same-plan-lines", domain.TradeSetLines)
	if err != nil || got.OpenAction != "same-plan-open" || got.Session != mine {
		t.Fatalf("got %+v err %v, want the plan's own dependency edge to win", got, err)
	}
}

// TestResolveTradeDependencyIgnoresOpenActions keeps an Open action free of
// both bindings: it opens the session rather than addressing one.
func TestResolveTradeDependencyIgnoresOpenActions(t *testing.T) {
	f := tradeResolutionFixture(t)
	ctx := context.Background()
	tradePlan(t, f.store, "trade-open-plan", []domain.Action{tradeOpenAction(t, "open-action-1")})
	tradePlan(t, f.store, "decoy-plan", []domain.Action{tradeOpenAction(t, "decoy-open")})
	if err := f.store.RecordTradeSessionReference(ctx, "open-action-1", "decoy-open"); err != nil {
		t.Fatal(err)
	}
	if err := f.store.RecordTradeSession(ctx, "decoy-open", store.TradeSession{SessionID: "s", SessionToken: "t"}); err != nil {
		t.Fatal(err)
	}
	got, err := f.executor.resolveTradeDependency(ctx, "trade-open-plan", "open-action-1", domain.TradeOpen)
	if err != nil || got != (TradeDependency{}) {
		t.Fatalf("got %+v err %v, want the zero dependency for an Open action", got, err)
	}
}

// TestTradeSessionReferenceIsImmutable is the safety property the cross-plan
// binding rests on: once an action is bound, nothing can re-point it at a
// different negotiation's Open action.
func TestTradeSessionReferenceIsImmutable(t *testing.T) {
	f := tradeResolutionFixture(t)
	ctx := context.Background()
	tradePlan(t, f.store, "trade-open-plan", []domain.Action{tradeOpenAction(t, "open-action-1")})
	tradePlan(t, f.store, "rival-plan", []domain.Action{tradeOpenAction(t, "rival-open")})
	tradePlan(t, f.store, "trade-lines-plan", []domain.Action{tradeLinesAction(t, "lines-action-1")})

	if _, ok, err := f.store.LookupTradeSessionReference(ctx, "lines-action-1"); err != nil || ok {
		t.Fatalf("ok %v err %v, want no binding before one is recorded", ok, err)
	}
	if err := f.store.RecordTradeSessionReference(ctx, "lines-action-1", "open-action-1"); err != nil {
		t.Fatal(err)
	}
	// Re-recording the identical pair is a no-op, so a retried submission is safe.
	if err := f.store.RecordTradeSessionReference(ctx, "lines-action-1", "open-action-1"); err != nil {
		t.Fatalf("re-recording the same binding must be idempotent: %v", err)
	}
	if err := f.store.RecordTradeSessionReference(ctx, "lines-action-1", "rival-open"); err == nil {
		t.Fatal("re-pointing a bound action at another negotiation's session must be rejected")
	}
	open, ok, err := f.store.LookupTradeSessionReference(ctx, "lines-action-1")
	if err != nil || !ok || open != "open-action-1" {
		t.Fatalf("got %q ok %v err %v, want the original binding intact", open, ok, err)
	}
	// An action that was never committed cannot be bound at all.
	if err = f.store.RecordTradeSessionReference(ctx, "ghost-action", "open-action-1"); err == nil {
		t.Fatal("binding an uncommitted action must be rejected")
	}
	// An action can never be bound to itself.
	if err = f.store.RecordTradeSessionReference(ctx, "lines-action-1", "lines-action-1"); err == nil {
		t.Fatal("binding an action to itself must be rejected")
	}
}
