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

type prisonerInteractionEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	effect                          domain.Effect
}

func (n *prisonerInteractionEnvironment) prisonerInteractionFacts(target Target) policy.PrisonerInteractionFacts {
	interaction, _ := target.Action.PrisonerInteraction()
	pawn := policy.PrisonerFacts{Pawn: interaction.Pawn(), SnapshotToken: "prisoner-token", Dead: domain.Known(false), Prisoner: domain.Known(true), Recruitable: domain.Known(true), CurrentInteraction: domain.Known(domain.PrisonerInteractionMaintain)}
	return policy.PrisonerInteractionFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, Pawn: pawn, NativeCanTry: domain.Known(!n.ineligible)}
}
func (n *prisonerInteractionEnvironment) InspectPrisonerInteraction(_ context.Context, target Target) (PrisonerInteractionInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return PrisonerInteractionInspection{StartedAt: now, ObservedAt: now, Facts: n.prisonerInteractionFacts(target)}, nil
}
func (n *prisonerInteractionEnvironment) WritePrisonerInteraction(_ context.Context, dispatch PrisonerInteractionDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *prisonerInteractionEnvironment) ObservePrisonerInteraction(_ context.Context, dispatch PrisonerInteractionDispatch, current domain.GenerationSnapshot) (PrisonerInteractionEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	pawn := dispatch.Admission.Pawn
	if n.foreign {
		pawn = "foreign-prisoner"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return PrisonerInteractionEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Pawn: pawn}, nil
}

func prisonerInteractionFixture(t *testing.T) (*fixture, *prisonerInteractionEnvironment) {
	t.Helper()
	f := newFixture(t)
	recruit, _ := domain.NewPrisonerInteraction("prisoner", domain.PrisonerInteractionRecruit)
	action, _ := domain.NewPrisonerInteractionAction("prisoner-interaction-1", recruit)
	plan, _ := domain.NewPlan("prisoner-interactions", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &prisonerInteractionEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnablePrisonerInteraction(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestPrisonerInteractionAdmitsAndDispatches(t *testing.T) {
	f, n := prisonerInteractionFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.PrisonerInteractionAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestPrisonerInteractionUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := prisonerInteractionFixture(t)
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
	f.executor.journal, f.executor.prisonerInteractionJournal = f.store, f.store
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

func TestPrisonerInteractionNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := prisonerInteractionFixture(t)
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

func TestPrisonerInteractionReconcileRejectsForeignPrisoner(t *testing.T) {
	f, n := prisonerInteractionFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
