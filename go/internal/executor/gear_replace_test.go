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

type gearReplaceEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	effect                          domain.Effect
}

func (n *gearReplaceEnvironment) gearReplaceFacts(target Target) policy.GearReplaceFacts {
	replace, _ := target.Action.GearReplace()
	pawn := policy.GearReplacePawnFacts{Pawn: replace.Pawn(), SnapshotToken: "pawn-token", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), ExistingJobDef: domain.Known("")}
	return policy.GearReplaceFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, Pawn: pawn, ThingSnapshotToken: "thing-token", LoadoutToken: "loadout-token", NativeCanTry: domain.Known(!n.ineligible)}
}
func (n *gearReplaceEnvironment) InspectGearReplace(_ context.Context, target Target) (GearReplaceInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return GearReplaceInspection{StartedAt: now, ObservedAt: now, Facts: n.gearReplaceFacts(target)}, nil
}
func (n *gearReplaceEnvironment) GearReplacePawn(_ context.Context, dispatch GearReplaceDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *gearReplaceEnvironment) ObserveGearReplace(_ context.Context, dispatch GearReplaceDispatch, current domain.GenerationSnapshot) (GearReplaceEvidence, error) {
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
	return GearReplaceEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Pawn: pawn, Thing: thing}, nil
}

func gearReplaceFixture(t *testing.T) (*fixture, *gearReplaceEnvironment) {
	t.Helper()
	f := newFixture(t)
	replace, _ := domain.NewGearReplace("unarmed", "thing", "Apparel_Parka")
	action, _ := domain.NewGearReplaceAction("gear-replace-1", replace)
	plan, _ := domain.NewPlan("gear-replaces", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &gearReplaceEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableGearReplace(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestGearReplaceAdmitsAndDispatches(t *testing.T) {
	f, n := gearReplaceFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.GearReplaceAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestGearReplaceUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := gearReplaceFixture(t)
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
	f.executor.journal, f.executor.gearReplaceJournal = f.store, f.store
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

func TestGearReplaceNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := gearReplaceFixture(t)
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

func TestGearReplaceReconcileRejectsForeignThing(t *testing.T) {
	f, n := gearReplaceFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
