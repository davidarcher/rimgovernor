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

type haulEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	absent                          bool
	effect                          domain.Effect
}

func (n *haulEnvironment) haulFacts(target Target) policy.HaulFacts {
	haul, _ := target.Action.Haul()
	pawn := policy.HaulPawnFacts{Pawn: haul.Pawn(), SnapshotToken: "pawn-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), ExistingJobDef: domain.Known("")}
	facts := policy.HaulFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, Pawn: pawn, ThingSnapshotToken: "thing-token", NativeCanTry: domain.Known(!n.ineligible)}
	if n.absent {
		facts.ThingPresent, facts.ThingSnapshotToken, facts.NativeCanTry = domain.Known(false), "", domain.Known(false)
	}
	return facts
}
func (n *haulEnvironment) InspectHaul(_ context.Context, target Target) (HaulInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return HaulInspection{StartedAt: now, ObservedAt: now, Facts: n.haulFacts(target)}, nil
}
func (n *haulEnvironment) HaulThing(_ context.Context, dispatch HaulDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *haulEnvironment) ObserveHaul(_ context.Context, dispatch HaulDispatch, current domain.GenerationSnapshot) (HaulEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	pawn, thing := dispatch.Admission.Pawn, dispatch.Admission.Thing
	if n.foreign {
		thing = "foreign-thing"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return HaulEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Pawn: pawn, Thing: thing}, nil
}

func haulFixture(t *testing.T) (*fixture, *haulEnvironment) {
	t.Helper()
	f := newFixture(t)
	haul, _ := domain.NewHaul("hauler", "thing", "MealSimple", domain.Cell{X: 1, Z: 1})
	action, _ := domain.NewHaulAction("haul-1", haul)
	plan, _ := domain.NewPlan("hauls", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &haulEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableHaul(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestHaulAdmitsAndDispatches(t *testing.T) {
	f, n := haulFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.HaulAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestHaulUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := haulFixture(t)
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
	f.executor.journal, f.executor.haulJournal = f.store, f.store
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

func TestHaulNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := haulFixture(t)
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

// A thing that left its cell can never be hauled by this proposal; the action
// settles as cancelled so the goal's method slot frees for a fresh target.
func TestHaulAbsentThingCancelsTheAction(t *testing.T) {
	f, n := haulFixture(t)
	n.absent = true
	result, err := f.run()
	if !errors.Is(err, ErrHeld) || result.Progress.View().Stage != domain.Cancelled || n.dispatched != 0 {
		t.Fatal(result, err)
	}
}

func TestHaulReconcileRejectsForeignThing(t *testing.T) {
	f, n := haulFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
