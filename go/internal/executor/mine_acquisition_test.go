package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"testing"
	"time"
)

// mineAcquisitionEnvironment mirrors acquisitionEnvironment exactly, proving
// the second, independently-registered mine-acquisition vertical exercises
// the identical inspect/admit/dispatch/reconcile state machine through its
// own boundary/journal wiring rather than sharing AcquisitionAction's.
type mineAcquisitionEnvironment struct {
	*environment
	inspected, allowed, observed       int
	uncertain, unsafe, foreign, absent bool
	onInspect                          func()
	effect                             domain.Effect
}

func (n *mineAcquisitionEnvironment) InspectAcquisition(_ context.Context, target Target) (AcquisitionInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	acquisition, _ := target.Action.MineAcquisition()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true)})
	return AcquisitionInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Acquisition: acquisition, SnapshotToken: "mine-token", Accepted: true, Emergency: emergency}, nil
}
func (n *mineAcquisitionEnvironment) Acquire(_ context.Context, request AcquisitionDispatch) (Receipt, error) {
	n.allowed++
	if request.SnapshotToken != "mine-token" {
		return Receipt{}, ErrEvidence
	}
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *mineAcquisitionEnvironment) ObserveAcquisition(_ context.Context, p Placement, current domain.GenerationSnapshot) (AcquisitionEvidence, error) {
	n.observed++
	acquisition, _ := p.Action.MineAcquisition()
	if n.foreign {
		acquisition, _ = domain.NewAcquisition("foreign", acquisition.Definition(), acquisition.Cell())
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	if n.absent {
		effect = domain.EffectAbsent
	}
	return AcquisitionEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: effect != domain.EffectUnknown, Acquisition: acquisition, LaborFinished: effect == domain.EffectCompleted || effect == domain.EffectUnsuccessful, OutputComplete: true, OutputObserved: effect == domain.EffectCompleted, ProducedUnits: 10}, nil
}
func mineAcquisitionFixture(t *testing.T) (*fixture, *mineAcquisitionEnvironment) {
	t.Helper()
	f := newFixture(t)
	acquisition, _ := domain.NewAcquisition("Rock1", "Steel", domain.Cell{X: 3, Z: 4})
	action, _ := domain.NewMineAcquisitionAction("mine-1", acquisition)
	plan, _ := domain.NewPlan("mining", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &mineAcquisitionEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableMineAcquisition(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}
func TestMineAcquisitionUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := mineAcquisitionFixture(t)
	n.uncertain = true
	result, err := f.run()
	if err == nil || !result.Progress.View().Unresolved || n.allowed != 1 || n.inspected != 2 {
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
	f.executor.journal, f.executor.mineAcquisitionJournal = f.store, f.store
	f.authority.Enabled = false
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	if _, err = f.run(); err != nil {
		t.Fatal(err)
	}
	if n.allowed != 1 || n.observed != 1 {
		t.Fatal("uncertainty retried")
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.allowed != 1 {
		t.Fatal(result, err)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.MineAcquisitionAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
}
func TestMineAcquisitionEmergencyAndDirectionChangesBlockDispatch(t *testing.T) {
	for _, changed := range []bool{false, true} {
		t.Run(map[bool]string{false: "unknown emergency", true: "player direction"}[changed], func(t *testing.T) {
			f, n := mineAcquisitionFixture(t)
			if changed {
				n.onInspect = func() { f.authority.Enabled = false; _ = f.executor.UpdateAuthority(f.authority) }
			} else {
				n.unsafe = true
			}
			if _, err := f.run(); err == nil || n.allowed != 0 {
				t.Fatal("unsafe write", err)
			}
			if changed {
				return
			}
			held, ok := f.progress(t).FreshHeldReason()
			if !ok || len(held) != 1 || held[0] != domain.HeldUnknownFacts {
				t.Fatal("emergency hold was not persisted as a held reason", held)
			}
		})
	}
}
func TestMineAcquisitionRejectsForeignCompletionAndAbsence(t *testing.T) {
	f, n := mineAcquisitionFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect, n.foreign = domain.EffectCompleted, true
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal(err)
	}
	n.foreign, n.absent = false, true
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal(err)
	}
	if !f.progress(t).Unresolved || n.allowed != 1 {
		t.Fatal("bad evidence released uncertainty")
	}
}
func TestMineAcquisitionTypedPreparationCannotBeBypassed(t *testing.T) {
	f, _ := mineAcquisitionFixture(t)
	if _, err := f.store.Prepare(context.Background(), f.plan.ID(), f.action.ID(), f.authority.Snapshot, 100); err == nil {
		t.Fatal("generic preparation bypassed snapshot")
	}
	wrong := store.MineAcquisitionAdmission{Snapshot: f.authority.Snapshot, Tick: 100, Thing: "other", SnapshotToken: "token"}
	if _, err := f.store.PrepareMineAcquisition(context.Background(), f.plan.ID(), f.action.ID(), wrong); err == nil {
		t.Fatal("foreign item admitted")
	}
}
