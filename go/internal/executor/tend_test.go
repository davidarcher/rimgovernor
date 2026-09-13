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

type tendEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, unsafe, foreign      bool
	onInspect                       func()
	effect                          domain.Effect
}

func (n *tendEnvironment) tendFacts(target Target) policy.TendFacts {
	tend, _ := target.Action.Tend()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{
		ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true),
		Colonists: []policy.EmergencyPawn{
			{ID: policy.PawnID(tend.Doctor()), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
			{ID: policy.PawnID(tend.Patient()), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)},
		},
	})
	doctor := policy.TendDoctorFacts{Pawn: tend.Doctor(), SnapshotToken: "doctor-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known(""), MedicineSkill: domain.Known(int32(8)), MedicineSkillDisabled: domain.Known(false), DoctorWorkEnabled: domain.Known(true), DoctorWorkOverrideDisabled: domain.Known(false)}
	patient := policy.TendPatientFacts{Pawn: tend.Patient(), SnapshotToken: "patient-token", Dead: domain.Known(false), Downed: domain.Known(false), NeedsTend: domain.Known(true), NoCare: domain.Known(false), Bleeding: domain.Known(true), LifeThreatening: domain.Known(false), HoursUntilDeathFromBloodLoss: domain.Known(6.0), ExistingJobDef: domain.Known("")}
	return policy.TendFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, NativeCanTry: domain.Known(true), Emergency: emergency, Doctor: doctor, Patient: patient}
}
func (n *tendEnvironment) InspectTend(_ context.Context, target Target) (TendInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	now := n.clock.Now()
	return TendInspection{StartedAt: now, ObservedAt: now, Facts: n.tendFacts(target)}, nil
}
func (n *tendEnvironment) TendPatient(_ context.Context, dispatch TendDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *tendEnvironment) ObserveTend(_ context.Context, dispatch TendDispatch, current domain.GenerationSnapshot) (TendEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	doctor, patient := dispatch.Admission.Doctor, dispatch.Admission.Patient
	if n.foreign {
		patient = "foreign-patient"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return TendEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Doctor: doctor, Patient: patient}, nil
}
func causalityFor(effect domain.Effect) domain.ObservationCausality {
	switch effect {
	case domain.EffectCompleted, domain.EffectAbsent, domain.EffectUnsuccessful:
		return domain.AfterDispatch
	default:
		return ""
	}
}

func tendFixture(t *testing.T) (*fixture, *tendEnvironment) {
	t.Helper()
	f := newFixture(t)
	tend, _ := domain.NewTend("doctor", "patient")
	action, _ := domain.NewTendAction("tend-1", tend)
	plan, _ := domain.NewPlan("tends", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &tendEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableTend(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestTendAdmitsAndDispatches(t *testing.T) {
	f, n := tendFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.TendAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestTendUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := tendFixture(t)
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
	f.executor.journal, f.executor.tendJournal = f.store, f.store
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

func TestTendUnsafeEmergencyBlocksDispatch(t *testing.T) {
	f, n := tendFixture(t)
	n.unsafe = true
	result, err := f.run()
	if err != ErrHeld || result.Progress.View().Stage != domain.Pending || n.dispatched != 0 {
		t.Fatal(result, err)
	}
}

func TestTendReconcileRejectsForeignPatient(t *testing.T) {
	f, n := tendFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
