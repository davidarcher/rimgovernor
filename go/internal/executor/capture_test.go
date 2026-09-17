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

type captureEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, unsafe, foreign      bool
	onInspect                       func()
	effect                          domain.Effect
}

func (n *captureEnvironment) captureFacts(target Target) policy.CaptureFacts {
	capture, _ := target.Action.Capture()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{
		ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true),
		Colonists: []policy.EmergencyPawn{
			{ID: policy.PawnID(capture.Capturer()), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		},
	})
	capturer := policy.RescuerFacts{Pawn: capture.Capturer(), SnapshotToken: "capturer-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	patient := policy.CapturePatientFacts{Pawn: capture.Patient(), SnapshotToken: "patient-token", Dead: domain.Known(false), Downed: domain.Known(true), Prisoner: domain.Known(false), ExistingJobDef: domain.Known("")}
	return policy.CaptureFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, NativeCanTry: domain.Known(true), Emergency: emergency, Capturer: capturer, Patient: patient}
}
func (n *captureEnvironment) InspectCapture(_ context.Context, target Target) (CaptureInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	now := n.clock.Now()
	return CaptureInspection{StartedAt: now, ObservedAt: now, Facts: n.captureFacts(target)}, nil
}
func (n *captureEnvironment) CapturePatient(_ context.Context, dispatch CaptureDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *captureEnvironment) ObserveCapture(_ context.Context, dispatch CaptureDispatch, current domain.GenerationSnapshot) (CaptureEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	capturer, patient := dispatch.Admission.Capturer, dispatch.Admission.Patient
	if n.foreign {
		patient = "foreign-patient"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return CaptureEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Capturer: capturer, Patient: patient}, nil
}

func captureFixture(t *testing.T) (*fixture, *captureEnvironment) {
	t.Helper()
	f := newFixture(t)
	capture, _ := domain.NewCapture("capturer", "patient")
	action, _ := domain.NewCaptureAction("capture-1", capture)
	plan, _ := domain.NewPlan("captures", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &captureEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableCapture(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestCaptureAdmitsAndDispatches(t *testing.T) {
	f, n := captureFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.CaptureAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestCaptureUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := captureFixture(t)
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
	f.executor.journal, f.executor.captureJournal = f.store, f.store
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

func TestCaptureUnsafeEmergencyBlocksDispatch(t *testing.T) {
	f, n := captureFixture(t)
	n.unsafe = true
	result, err := f.run()
	if err != ErrHeld || result.Progress.View().Stage != domain.Pending || n.dispatched != 0 {
		t.Fatal(result, err)
	}
}

func TestCaptureReconcileRejectsForeignPatient(t *testing.T) {
	f, n := captureFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
