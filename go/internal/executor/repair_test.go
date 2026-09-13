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

type repairEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, foreign              bool
	onInspect                       func()
	effect                          domain.Effect
}

func (n *repairEnvironment) repairFacts(target Target) policy.RepairFacts {
	repair, _ := target.Action.Repair()
	pawn := policy.RepairPawnFacts{Pawn: repair.Pawn(), SnapshotToken: "pawn-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	structure := policy.RepairStructureFacts{Structure: repair.Structure(), SnapshotToken: "structure-token", Exists: domain.Known(true), Damaged: domain.Known(true)}
	return policy.RepairFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, NativeCanTry: domain.Known(true), Pawn: pawn, Structure: structure}
}
func (n *repairEnvironment) InspectRepair(_ context.Context, target Target) (RepairInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	now := n.clock.Now()
	return RepairInspection{StartedAt: now, ObservedAt: now, Facts: n.repairFacts(target)}, nil
}
func (n *repairEnvironment) RepairStructure(_ context.Context, dispatch RepairDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *repairEnvironment) ObserveRepair(_ context.Context, dispatch RepairDispatch, current domain.GenerationSnapshot) (RepairEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	pawn, structure := dispatch.Admission.Pawn, dispatch.Admission.Structure
	if n.foreign {
		structure = "foreign-structure"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return RepairEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Pawn: pawn, Structure: structure}, nil
}

func repairFixture(t *testing.T) (*fixture, *repairEnvironment) {
	t.Helper()
	f := newFixture(t)
	repair, _ := domain.NewRepair("repairer", "wall", domain.Cell{X: 1, Z: 1})
	action, _ := domain.NewRepairAction("repair-1", repair)
	plan, _ := domain.NewPlan("repairs", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &repairEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableRepair(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestRepairAdmitsAndDispatches(t *testing.T) {
	f, n := repairFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.RepairAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestRepairUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := repairFixture(t)
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
	f.executor.journal, f.executor.repairJournal = f.store, f.store
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

func TestRepairReconcileRejectsForeignStructure(t *testing.T) {
	f, n := repairFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
