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

type equipEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	effect                          domain.Effect
}

func (n *equipEnvironment) equipFacts(target Target) policy.EquipFacts {
	equip, _ := target.Action.Equip()
	pawn := policy.EquipPawnFacts{Pawn: equip.Pawn(), SnapshotToken: "pawn-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	return policy.EquipFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, Pawn: pawn, ThingSnapshotToken: "thing-token", NativeCanTry: domain.Known(!n.ineligible)}
}
func (n *equipEnvironment) InspectEquip(_ context.Context, target Target) (EquipInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return EquipInspection{StartedAt: now, ObservedAt: now, Facts: n.equipFacts(target)}, nil
}
func (n *equipEnvironment) EquipPawn(_ context.Context, dispatch EquipDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *equipEnvironment) ObserveEquip(_ context.Context, dispatch EquipDispatch, current domain.GenerationSnapshot) (EquipEvidence, error) {
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
	return EquipEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Pawn: pawn, Thing: thing}, nil
}

func equipFixture(t *testing.T) (*fixture, *equipEnvironment) {
	t.Helper()
	f := newFixture(t)
	equip, _ := domain.NewEquip("unarmed", "thing", "Gun_Revolver", domain.Cell{X: 1, Z: 1})
	action, _ := domain.NewEquipAction("equip-1", equip)
	plan, _ := domain.NewPlan("equips", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &equipEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableEquip(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestEquipAdmitsAndDispatches(t *testing.T) {
	f, n := equipFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.EquipAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestEquipUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := equipFixture(t)
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
	f.executor.journal, f.executor.equipJournal = f.store, f.store
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

func TestEquipNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := equipFixture(t)
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

func TestEquipReconcileRejectsForeignThing(t *testing.T) {
	f, n := equipFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
