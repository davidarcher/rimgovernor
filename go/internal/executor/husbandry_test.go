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

type husbandryEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	effect                          domain.Effect
}

func (n *husbandryEnvironment) husbandryFacts(target Target) policy.HusbandryFacts {
	husbandry, _ := target.Action.Husbandry()
	animal := policy.HusbandryAnimalFacts{Animal: husbandry.Animal(), SnapshotToken: "animal-token", Dead: domain.Known(false), CanTrain: domain.Known(true), Learned: domain.Known(false), SafeToSlaughter: domain.Known(true)}
	return policy.HusbandryFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, Animal: animal, CensusToken: "census-token", NativeCanTry: domain.Known(!n.ineligible)}
}
func (n *husbandryEnvironment) InspectHusbandry(_ context.Context, target Target) (HusbandryInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return HusbandryInspection{StartedAt: now, ObservedAt: now, Facts: n.husbandryFacts(target)}, nil
}
func (n *husbandryEnvironment) WriteHusbandry(_ context.Context, dispatch HusbandryDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *husbandryEnvironment) ObserveHusbandry(_ context.Context, dispatch HusbandryDispatch, current domain.GenerationSnapshot) (HusbandryEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	animal := dispatch.Admission.Animal
	if n.foreign {
		animal = "foreign-animal"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return HusbandryEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Animal: animal}, nil
}

func husbandryFixture(t *testing.T) (*fixture, *husbandryEnvironment) {
	t.Helper()
	f := newFixture(t)
	train, _ := domain.NewHusbandry("animal", domain.HusbandryTrain, "Trainability_Advanced")
	action, _ := domain.NewHusbandryAction("husbandry-1", train)
	plan, _ := domain.NewPlan("husbandries", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &husbandryEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableHusbandry(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestHusbandryAdmitsAndDispatches(t *testing.T) {
	f, n := husbandryFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.HusbandryAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestHusbandryUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := husbandryFixture(t)
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
	f.executor.journal, f.executor.husbandryJournal = f.store, f.store
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

func TestHusbandryNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := husbandryFixture(t)
	n.ineligible = true
	result, err := f.run()
	if err != ErrHeld || result.Progress.View().Stage != domain.Pending || n.dispatched != 0 {
		t.Fatal(result, err)
	}
}

func TestHusbandryReconcileRejectsForeignAnimal(t *testing.T) {
	f, n := husbandryFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
