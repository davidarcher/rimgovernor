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

type recoveryServiceEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	effect                          domain.Effect
}

func (n *recoveryServiceEnvironment) recoveryServiceFacts(target Target) policy.RecoveryServiceFacts {
	service, _ := target.Action.RecoveryService()
	pawn := policy.RecoveryServicePawnFacts{Pawn: service.Pawn(), SnapshotToken: "pawn-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	return policy.RecoveryServiceFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, Pawn: pawn, ThingSnapshotToken: "thing-token", NativeCanTry: domain.Known(!n.ineligible)}
}
func (n *recoveryServiceEnvironment) InspectRecoveryService(_ context.Context, target Target) (RecoveryServiceInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return RecoveryServiceInspection{StartedAt: now, ObservedAt: now, Facts: n.recoveryServiceFacts(target)}, nil
}
func (n *recoveryServiceEnvironment) RecoveryServicePawn(_ context.Context, dispatch RecoveryServiceDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *recoveryServiceEnvironment) ObserveRecoveryService(_ context.Context, dispatch RecoveryServiceDispatch, current domain.GenerationSnapshot) (RecoveryServiceEvidence, error) {
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
	return RecoveryServiceEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Pawn: pawn, Thing: thing}, nil
}

func recoveryServiceFixture(t *testing.T) (*fixture, *recoveryServiceEnvironment) {
	t.Helper()
	f := newFixture(t)
	service, _ := domain.NewRecoveryService("unarmed", "building", domain.RecoveryServiceRepair)
	action, _ := domain.NewRecoveryServiceAction("recovery-service-1", service)
	plan, _ := domain.NewPlan("recovery-services", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &recoveryServiceEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableRecoveryService(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestRecoveryServiceAdmitsAndDispatches(t *testing.T) {
	f, n := recoveryServiceFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.RecoveryServiceAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestRecoveryServiceUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := recoveryServiceFixture(t)
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
	f.executor.journal, f.executor.recoveryServiceJournal = f.store, f.store
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

func TestRecoveryServiceNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := recoveryServiceFixture(t)
	n.ineligible = true
	result, err := f.run()
	if err != ErrHeld || result.Progress.View().Stage != domain.Pending || n.dispatched != 0 {
		t.Fatal(result, err)
	}
}

func TestRecoveryServiceReconcileRejectsForeignThing(t *testing.T) {
	f, n := recoveryServiceFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
