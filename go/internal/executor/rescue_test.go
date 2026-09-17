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

type rescueEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, unsafe, foreign      bool
	onInspect                       func()
	effect                          domain.Effect
}

func (n *rescueEnvironment) rescueFacts(target Target) policy.RescueFacts {
	rescue, _ := target.Action.Rescue()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{
		ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true),
		Colonists: []policy.EmergencyPawn{
			{ID: policy.PawnID(rescue.Rescuer()), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
			{ID: policy.PawnID(rescue.Patient()), Dead: domain.Known(false), Downed: domain.Known(true), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)},
		},
	})
	rescuer := policy.RescuerFacts{Pawn: rescue.Rescuer(), SnapshotToken: "rescuer-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	patient := policy.RescuePatientFacts{Pawn: rescue.Patient(), SnapshotToken: "patient-token", Dead: domain.Known(false), Downed: domain.Known(true), InBed: domain.Known(false), BedID: domain.Unknown[string](), ExistingJobDef: domain.Known("")}
	return policy.RescueFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, NativeCanTry: domain.Known(true), Emergency: emergency, Rescuer: rescuer, Patient: patient}
}
func (n *rescueEnvironment) InspectRescue(_ context.Context, target Target) (RescueInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	now := n.clock.Now()
	return RescueInspection{StartedAt: now, ObservedAt: now, Facts: n.rescueFacts(target)}, nil
}
func (n *rescueEnvironment) RescuePatient(_ context.Context, dispatch RescueDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *rescueEnvironment) ObserveRescue(_ context.Context, dispatch RescueDispatch, current domain.GenerationSnapshot) (RescueEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	rescuer, patient := dispatch.Admission.Rescuer, dispatch.Admission.Patient
	if n.foreign {
		patient = "foreign-patient"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return RescueEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Rescuer: rescuer, Patient: patient}, nil
}

func rescueFixture(t *testing.T) (*fixture, *rescueEnvironment) {
	t.Helper()
	f := newFixture(t)
	rescue, _ := domain.NewRescue("rescuer", "patient")
	action, _ := domain.NewRescueAction("rescue-1", rescue)
	plan, _ := domain.NewPlan("rescues", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &rescueEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableRescue(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestRescueAdmitsAndDispatches(t *testing.T) {
	f, n := rescueFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.RescueAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestRescueUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := rescueFixture(t)
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
	f.executor.journal, f.executor.rescueJournal = f.store, f.store
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

func TestRescueUnsafeEmergencyBlocksDispatch(t *testing.T) {
	f, n := rescueFixture(t)
	n.unsafe = true
	result, err := f.run()
	if err != ErrHeld || result.Progress.View().Stage != domain.Pending || n.dispatched != 0 {
		t.Fatal(result, err)
	}
}

func TestRescueReconcileRejectsForeignPatient(t *testing.T) {
	f, n := rescueFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
