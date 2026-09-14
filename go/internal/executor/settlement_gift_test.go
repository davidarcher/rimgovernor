package executor

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type settlementGiftEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	effect                          domain.Effect
}

func (n *settlementGiftEnvironment) settlementGiftFacts(target Target) policy.SettlementGiftAdmissionFacts {
	gift, _ := target.Action.SettlementGift()
	facts := policy.SettlementGiftFacts{
		Caravan: gift.Caravan(), CaravanToken: "caravan-token", Moving: domain.Known(false),
		CrewIDs: domain.Known(gift.CrewIDs()), Silver: domain.Known(gift.Silver()),
		Settlement: gift.Settlement(), AtTarget: domain.Known(true),
		Faction: gift.Faction(), FactionToken: "faction-token", Player: domain.Known(false),
		Hostile: domain.Known(false), Goodwill: domain.Known(int32(10)),
	}
	return policy.SettlementGiftAdmissionFacts{Snapshot: target.Snapshot, CaravanTick: n.tick, WorldTick: n.tick, PreviewTick: n.tick, Gift: facts, NativeCanTry: domain.Known(!n.ineligible)}
}
func (n *settlementGiftEnvironment) InspectSettlementGift(_ context.Context, target Target) (SettlementGiftInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return SettlementGiftInspection{StartedAt: now, ObservedAt: now, Facts: n.settlementGiftFacts(target)}, nil
}
func (n *settlementGiftEnvironment) WriteSettlementGift(_ context.Context, dispatch SettlementGiftDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *settlementGiftEnvironment) ObserveSettlementGift(_ context.Context, dispatch SettlementGiftDispatch, current domain.GenerationSnapshot) (SettlementGiftEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	caravan := dispatch.Admission.Caravan
	if n.foreign {
		caravan = "foreign-caravan"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return SettlementGiftEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Caravan: caravan}, nil
}

func settlementGiftFixture(t *testing.T) (*fixture, *settlementGiftEnvironment) {
	t.Helper()
	f := newFixture(t)
	gift, _ := domain.NewSettlementGift("caravan-1", "settlement-1", "faction-1", []domain.PawnID{"pawn-1", "pawn-2"}, 500)
	action, _ := domain.NewSettlementGiftAction("settlement-gift-1", gift)
	plan, _ := domain.NewPlan("settlement-gifts", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &settlementGiftEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableSettlementGift(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestSettlementGiftAdmitsAndDispatches(t *testing.T) {
	f, n := settlementGiftFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.SettlementGiftAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestSettlementGiftUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := settlementGiftFixture(t)
	n.uncertain = true
	result, err := f.run()
	if err == nil || !result.Progress.View().Unresolved || n.dispatched != 1 || n.inspected != 2 {
		t.Fatal(result, err, n)
	}
	if err = f.store.Close(); err != nil {
		t.Fatal(err)
	}
	f.store, err = store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.store.Close()
	f.executor.journal, f.executor.settlementGiftJournal = f.store, f.store
	f.authority.Enabled = false
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	if _, err = f.run(); err != nil {
		t.Fatal(err)
	}
	if n.dispatched != 1 || n.observed != 1 {
		t.Fatal("uncertainty retried")
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err)
	}
}

func TestSettlementGiftNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := settlementGiftFixture(t)
	n.ineligible = true
	result, err := f.run()
	if err != ErrHeld || result.Progress.View().Stage != domain.Pending || n.dispatched != 0 {
		t.Fatal(result, err)
	}
	held, ok := result.Progress.View().FreshHeldReason()
	if !ok || len(held) != 1 || held[0] != domain.HeldNativeIneligible {
		t.Fatal("ordinary refusal was not persisted as a held reason", held)
	}
}

func TestSettlementGiftReconcileRejectsForeignCaravan(t *testing.T) {
	f, n := settlementGiftFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
