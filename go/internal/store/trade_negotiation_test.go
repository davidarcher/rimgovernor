package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func tradeEconomyRequest(id string) TradeEconomySubmissionRequest {
	return TradeEconomySubmissionRequest{
		RequestID: id, World: World{Colony: "colony", Load: "load", Map: 0},
		Trader: "settlement-1", Negotiator: "pawn-1",
		Policy: domain.TradeEconomicPolicy{
			Targets:       []domain.TradeTarget{{Item: "Steel", Stock: 500, MaxBuy: 200, MaxSell: 200, MaxBuyPrice: 3, MinSellPrice: 1}},
			SilverReserve: 300,
		},
		MaxSilverSpend: 1000,
	}
}

func tradeSetLinesStep(t *testing.T) TradeNegotiationStep {
	t.Helper()
	lines := []domain.TradeLine{{LineID: "line-1", AbsoluteCount: 40}}
	trade, err := domain.NewTradeSetLines(lines, false)
	if err != nil {
		t.Fatal(err)
	}
	return TradeNegotiationStep{
		Phase: TradeNegotiationPendingLines, Trade: trade, Selected: lines,
		Evidence: []policy.TradeSelectionEvidence{{Item: "Steel", Matched: true, EligibleStock: 100, RetainedTarget: 500, Count: 40}},
	}
}

func tradeAcceptStep(t *testing.T) TradeNegotiationStep {
	t.Helper()
	floors := []domain.TradeEconomicFloor{{DefName: "Silver", Count: 300}}
	trade, err := domain.NewTradeAccept("deal-1", floors, false, false)
	if err != nil {
		t.Fatal(err)
	}
	return TradeNegotiationStep{Phase: TradeNegotiationPendingAccept, Trade: trade, Floors: floors}
}

func tradeEndStep(t *testing.T, reason string) TradeNegotiationStep {
	t.Helper()
	trade, err := domain.NewTradeEnd(domain.TradeEndCancel, false)
	if err != nil {
		t.Fatal(err)
	}
	return TradeNegotiationStep{Phase: TradeNegotiationPendingEnd, Trade: trade, Reason: reason}
}

// TestTradeNegotiationRunsItsPhases walks one whole negotiation and checks the
// two properties every phase depends on: each phase is committed as its own
// one-action plan, and each successor action is durably bound to the Open
// action whose session it addresses.
func TestTradeNegotiationRunsItsPhases(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "trade-negotiation.db"))
	request := tradeEconomyRequest("request")

	first, created, err := s.SubmitTradeEconomy(ctx, request)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	if first.Phase != TradeNegotiationPendingOpen || first.Outcome != TradeNegotiationOpen {
		t.Fatalf("phase %q outcome %q, want a pending_open negotiation", first.Phase, first.Outcome)
	}
	if first.OpenPlan == "" || first.OpenAction == "" || first.CurrentPlan != first.OpenPlan || first.CurrentAction != first.OpenAction {
		t.Fatalf("%+v: the open phase must be the current phase", first)
	}
	// Open opens the session; it is bound to nothing.
	if _, ok, err := s.LookupTradeSessionReference(ctx, first.OpenAction); err != nil || ok {
		t.Fatalf("ok %v err %v, want the open action itself unbound", ok, err)
	}

	lines, err := s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, tradeSetLinesStep(t))
	if err != nil {
		t.Fatal(err)
	}
	if lines.Phase != TradeNegotiationPendingLines || lines.OpenAction != first.OpenAction {
		t.Fatalf("%+v: advancing must keep the same open action", lines)
	}
	if lines.CurrentPlan == first.OpenPlan || lines.CurrentAction == first.OpenAction {
		t.Fatalf("%+v: each phase needs its own plan and action", lines)
	}
	if len(lines.Selected) != 1 || lines.Selected[0].AbsoluteCount != 40 || len(lines.Evidence) != 1 {
		t.Fatalf("%+v: the step's decision was not retained", lines)
	}
	bound, ok, err := s.LookupTradeSessionReference(ctx, lines.CurrentAction)
	if err != nil || !ok || bound != first.OpenAction {
		t.Fatalf("got %q ok %v err %v, want the set-lines action bound to the open action", bound, ok, err)
	}
	// The phase's plan really was committed, as a one-action plan.
	state, err := s.LoadPlan(ctx, lines.CurrentPlan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Spec.Actions()[0].ID() != lines.CurrentAction {
		t.Fatalf("plan %+v err %v, want a committed one-action plan", state, err)
	}

	accept, err := s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingLines, tradeAcceptStep(t))
	if err != nil {
		t.Fatal(err)
	}
	if accept.Phase != TradeNegotiationPendingAccept || len(accept.Floors) != 1 || accept.Floors[0].Count != 300 {
		t.Fatalf("%+v: the accept phase did not carry its reserve floors", accept)
	}
	// The decision from the earlier phase survives untouched.
	if len(accept.Selected) != 1 || accept.Selected[0].LineID != "line-1" {
		t.Fatalf("%+v: an advance must not discard an earlier phase's decision", accept)
	}
	if bound, ok, err = s.LookupTradeSessionReference(ctx, accept.CurrentAction); err != nil || !ok || bound != first.OpenAction {
		t.Fatalf("got %q ok %v err %v, want the accept action bound to the open action", bound, ok, err)
	}

	done, err := s.FinalizeTradeNegotiation(ctx, "request", TradeNegotiationPendingAccept, TradeNegotiationAccepted, "", -120.5, nil)
	if err != nil {
		t.Fatal(err)
	}
	if done.Phase != TradeNegotiationDone || done.Outcome != TradeNegotiationAccepted || done.NetSilver != -120.5 {
		t.Fatalf("%+v, want a done/accepted negotiation carrying its net silver", done)
	}
	if done.OpenAction != first.OpenAction || len(done.Selected) != 1 {
		t.Fatalf("%+v: finishing must not lose what the negotiation decided", done)
	}
}

func TestTradeNegotiationSurvivesReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "trade-negotiation-reopen.db")
	s := open(t, path)
	request := tradeEconomyRequest("request")
	first, _, err := s.SubmitTradeEconomy(ctx, request)
	if err != nil {
		t.Fatal(err)
	}
	lines, err := s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, tradeSetLinesStep(t))
	if err != nil {
		t.Fatal(err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)

	found, err := s.LookupTradeEconomy(ctx, "request")
	if err != nil {
		t.Fatal(err)
	}
	if found.Phase != TradeNegotiationPendingLines || found.OpenAction != first.OpenAction || found.CurrentAction != lines.CurrentAction {
		t.Fatalf("%+v, want the in-flight phase restored", found)
	}
	if !sameTradeEconomyRequest(found.Request, request) {
		t.Fatalf("request %+v, want the submitted request verbatim", found.Request)
	}
	if len(found.Selected) != 1 || found.Selected[0].LineID != "line-1" || len(found.Evidence) != 1 || found.Evidence[0].Item != "Steel" {
		t.Fatalf("%+v: the decision did not survive a reopen", found)
	}

	pending, err := s.UnfinishedTradeNegotiations(ctx, request.World)
	if err != nil || len(pending) != 1 || pending[0].Request.RequestID != "request" {
		t.Fatalf("pending %+v err %v, want the one unfinished negotiation", pending, err)
	}
	// Another world's driver must not pick it up.
	if pending, err = s.UnfinishedTradeNegotiations(ctx, World{Colony: "other", Load: "load", Map: 0}); err != nil || len(pending) != 0 {
		t.Fatalf("pending %+v err %v, want nothing for another world", pending, err)
	}
	if _, err = s.FinalizeTradeNegotiation(ctx, "request", TradeNegotiationPendingLines, TradeNegotiationNoTrade, "nothing worth trading", 0, nil); err != nil {
		t.Fatal(err)
	}
	if pending, err = s.UnfinishedTradeNegotiations(ctx, request.World); err != nil || len(pending) != 0 {
		t.Fatalf("pending %+v err %v, want a finished negotiation to stop being ticked", pending, err)
	}
}

// TestTradeNegotiationCancelsWithoutTrading covers the other terminal path: a
// negotiation that decided against the deal still owes native an explicit End,
// which is a committed and bound phase like any other.
func TestTradeNegotiationCancelsWithoutTrading(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "trade-negotiation-cancel.db"))
	first, _, err := s.SubmitTradeEconomy(ctx, tradeEconomyRequest("request"))
	if err != nil {
		t.Fatal(err)
	}
	end, err := s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, tradeEndStep(t, "nothing worth trading"))
	if err != nil {
		t.Fatal(err)
	}
	if end.Phase != TradeNegotiationPendingEnd || end.Reason != "nothing worth trading" || len(end.Selected) != 0 {
		t.Fatalf("%+v, want a pending_end negotiation that staged nothing", end)
	}
	bound, ok, err := s.LookupTradeSessionReference(ctx, end.CurrentAction)
	if err != nil || !ok || bound != first.OpenAction {
		t.Fatalf("got %q ok %v err %v, want the end action bound to the open action", bound, ok, err)
	}
	done, err := s.FinalizeTradeNegotiation(ctx, "request", TradeNegotiationPendingEnd, TradeNegotiationNoTrade, "", 0, nil)
	if err != nil || done.Phase != TradeNegotiationDone || done.Outcome != TradeNegotiationNoTrade || done.Reason != "nothing worth trading" {
		t.Fatalf("%+v err %v, want a done/no_trade negotiation keeping its reason", done, err)
	}
}

// TestTradeNegotiationAdvanceIsGuardedOnItsPhase is the property that keeps two
// drivers (or a driver and its own retry) from opening a second deal: an
// advance names the phase it believed the negotiation was in, and loses if it
// was wrong.
func TestTradeNegotiationAdvanceIsGuardedOnItsPhase(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "trade-negotiation-guard.db"))
	if _, _, err := s.SubmitTradeEconomy(ctx, tradeEconomyRequest("request")); err != nil {
		t.Fatal(err)
	}
	won, err := s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, tradeSetLinesStep(t))
	if err != nil {
		t.Fatal(err)
	}
	// The same advance run twice must not commit a second set-lines plan.
	if _, err = s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, tradeSetLinesStep(t)); !errors.Is(err, ErrConflict) {
		t.Fatalf("err %v, want ErrConflict for a stale advance", err)
	}
	if _, err = s.FinalizeTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, TradeNegotiationNoTrade, "stale", 0, nil); !errors.Is(err, ErrConflict) {
		t.Fatalf("err %v, want ErrConflict for a stale finalize", err)
	}
	found, err := s.LookupTradeEconomy(ctx, "request")
	if err != nil || found.CurrentAction != won.CurrentAction || found.Phase != TradeNegotiationPendingLines {
		t.Fatalf("%+v err %v, want the losing advance to have changed nothing", found, err)
	}
	// A finished negotiation cannot be advanced at all.
	if _, err = s.FinalizeTradeNegotiation(ctx, "request", TradeNegotiationPendingLines, TradeNegotiationAccepted, "", -5, nil); err != nil {
		t.Fatal(err)
	}
	if _, err = s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationDone, tradeAcceptStep(t)); err == nil {
		t.Fatal("a finished negotiation must refuse further phases")
	}
	if _, err = s.FinalizeTradeNegotiation(ctx, "request", TradeNegotiationDone, TradeNegotiationRefused, "", 0, nil); err == nil {
		t.Fatal("a finished negotiation must refuse a second outcome")
	}
	found, err = s.LookupTradeEconomy(ctx, "request")
	if err != nil || found.Outcome != TradeNegotiationAccepted || found.NetSilver != -5 {
		t.Fatalf("%+v err %v, want the settled outcome untouched", found, err)
	}
}

func TestTradeNegotiationRejectsInvalidStepsAndRequests(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "trade-negotiation-invalid.db"))

	for name, spoil := range map[string]func(*TradeEconomySubmissionRequest){
		"no targets":       func(q *TradeEconomySubmissionRequest) { q.Policy.Targets = nil },
		"no trader":        func(q *TradeEconomySubmissionRequest) { q.Trader = "" },
		"no negotiator":    func(q *TradeEconomySubmissionRequest) { q.Negotiator = "" },
		"negative spend":   func(q *TradeEconomySubmissionRequest) { q.MaxSilverSpend = -1 },
		"negative reserve": func(q *TradeEconomySubmissionRequest) { q.Policy.SilverReserve = -1 },
		"session row item": func(q *TradeEconomySubmissionRequest) { q.Policy.Targets[0].Item = "#3" },
		"duplicate target": func(q *TradeEconomySubmissionRequest) {
			q.Policy.Targets = append(q.Policy.Targets, domain.TradeTarget{Item: "steel", Stock: 1, MaxBuy: 1})
		},
		"no world": func(q *TradeEconomySubmissionRequest) { q.World = World{} },
	} {
		t.Run(name, func(t *testing.T) {
			q := tradeEconomyRequest("request-" + name)
			q.Policy.Targets = append([]domain.TradeTarget(nil), q.Policy.Targets...)
			spoil(&q)
			if _, _, err := s.SubmitTradeEconomy(ctx, q); err == nil {
				t.Fatal("accepted an invalid trade economy request")
			}
		})
	}

	if _, _, err := s.SubmitTradeEconomy(ctx, tradeEconomyRequest("request")); err != nil {
		t.Fatal(err)
	}
	// A negotiation opens exactly once: a second Open is not a phase.
	open2, err := domain.NewTradeOpen("settlement-1", "pawn-1", false)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, TradeNegotiationStep{Phase: TradeNegotiationPendingLines, Trade: open2}); err == nil {
		t.Fatal("accepted a second open as a phase")
	}
	for name, phase := range map[string]TradeNegotiationPhase{
		"back to open":     TradeNegotiationPendingOpen,
		"straight to done": TradeNegotiationDone,
		"nonsense":         TradeNegotiationPhase("halfway"),
	} {
		t.Run(name, func(t *testing.T) {
			step := tradeSetLinesStep(t)
			step.Phase = phase
			if _, err := s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, step); err == nil {
				t.Fatalf("accepted an advance into %q", phase)
			}
		})
	}
	// Finishing needs an explicit outcome, never the open-ended zero one.
	if _, err = s.FinalizeTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, TradeNegotiationOpen, "", 0, nil); err == nil {
		t.Fatal("accepted a finish with no outcome")
	}
	if _, err = s.LookupTradeEconomy(ctx, "never-submitted"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("err %v, want ErrNotFound", err)
	}
}

func TestTradeEconomySubmissionReplayAndConflict(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "trade-economy-replay.db"))
	request := tradeEconomyRequest("request")
	first, created, err := s.SubmitTradeEconomy(ctx, request)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	replay, created, err := s.SubmitTradeEconomy(ctx, request)
	if err != nil || created || replay.OpenAction != first.OpenAction || replay.OpenPlan != first.OpenPlan {
		t.Fatalf("%+v created %v err %v, want an idempotent replay", replay, created, err)
	}
	for name, change := range map[string]func(*TradeEconomySubmissionRequest){
		"other trader":     func(q *TradeEconomySubmissionRequest) { q.Trader = "settlement-2" },
		"other negotiator": func(q *TradeEconomySubmissionRequest) { q.Negotiator = "pawn-2" },
		"other world":      func(q *TradeEconomySubmissionRequest) { q.World.Map = 1 },
		"other spend":      func(q *TradeEconomySubmissionRequest) { q.MaxSilverSpend = 999 },
		"other reserve":    func(q *TradeEconomySubmissionRequest) { q.Policy.SilverReserve = 299 },
		"other target":     func(q *TradeEconomySubmissionRequest) { q.Policy.Targets[0].MaxBuy = 199 },
	} {
		t.Run(name, func(t *testing.T) {
			changed := request
			changed.Policy.Targets = append([]domain.TradeTarget(nil), request.Policy.Targets...)
			change(&changed)
			if _, _, err := s.SubmitTradeEconomy(ctx, changed); !errors.Is(err, ErrConflict) {
				t.Fatalf("err %v, want ErrConflict when a request ID is reused with different terms", err)
			}
		})
	}
	// A replay after phases have advanced returns the live negotiation, not a
	// fresh one: the same request ID never opens a second deal.
	if _, err = s.AdvanceTradeNegotiation(ctx, "request", TradeNegotiationPendingOpen, tradeSetLinesStep(t)); err != nil {
		t.Fatal(err)
	}
	replay, created, err = s.SubmitTradeEconomy(ctx, request)
	if err != nil || created || replay.Phase != TradeNegotiationPendingLines || replay.OpenAction != first.OpenAction {
		t.Fatalf("%+v created %v err %v, want the live negotiation back", replay, created, err)
	}
}
