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

type cleanEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, foreign              bool
	onInspect                       func()
	effect                          domain.Effect
}

func (n *cleanEnvironment) cleanFacts(target Target) policy.CleanFacts {
	clean, _ := target.Action.Clean()
	pawn := policy.CleanPawnFacts{Pawn: clean.Pawn(), SnapshotToken: "pawn-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), ExistingJobDef: domain.Known("")}
	filth := policy.CleanFilthFacts{Filth: clean.Filth(), SnapshotToken: "filth-token", Exists: domain.Known(true)}
	return policy.CleanFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, NativeCanTry: domain.Known(true), Pawn: pawn, Filth: filth}
}
func (n *cleanEnvironment) InspectClean(_ context.Context, target Target) (CleanInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	now := n.clock.Now()
	return CleanInspection{StartedAt: now, ObservedAt: now, Facts: n.cleanFacts(target)}, nil
}
func (n *cleanEnvironment) CleanFilth(_ context.Context, dispatch CleanDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *cleanEnvironment) ObserveClean(_ context.Context, dispatch CleanDispatch, current domain.GenerationSnapshot) (CleanEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	pawn, filth := dispatch.Admission.Pawn, dispatch.Admission.Filth
	if n.foreign {
		filth = "foreign-filth"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return CleanEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Pawn: pawn, Filth: filth}, nil
}

func cleanFixture(t *testing.T) (*fixture, *cleanEnvironment) {
	t.Helper()
	f := newFixture(t)
	clean, _ := domain.NewClean("cleaner", "filth", domain.Cell{X: 1, Z: 1})
	action, _ := domain.NewCleanAction("clean-1", clean)
	plan, _ := domain.NewPlan("cleans", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &cleanEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableClean(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestCleanAdmitsAndDispatches(t *testing.T) {
	f, n := cleanFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.CleanAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestCleanUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := cleanFixture(t)
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
	f.executor.journal, f.executor.cleanJournal = f.store, f.store
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

func TestCleanReconcileRejectsForeignFilth(t *testing.T) {
	f, n := cleanFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
