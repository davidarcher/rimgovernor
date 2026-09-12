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

type supplyEnvironment struct {
	*environment
	inspected, allowed, observed       int
	uncertain, unsafe, foreign, absent bool
	onInspect                          func()
	effect                             domain.Effect
}

func (n *supplyEnvironment) InspectSupply(_ context.Context, target Target) (SupplyInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	supply, _ := target.Action.SupplyAllow()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true)})
	return SupplyInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Supply: supply, SnapshotToken: "supply-token", Accepted: true, Emergency: emergency}, nil
}
func (n *supplyEnvironment) AllowSupply(_ context.Context, request SupplyDispatch) (Receipt, error) {
	n.allowed++
	if request.SnapshotToken != "supply-token" {
		return Receipt{}, ErrEvidence
	}
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *supplyEnvironment) ObserveSupply(_ context.Context, p Placement, current domain.GenerationSnapshot) (SupplyEvidence, error) {
	n.observed++
	supply, _ := p.Action.SupplyAllow()
	if n.foreign {
		supply, _ = domain.NewSupplyAllow("foreign", supply.Definition(), supply.Cell())
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	if n.absent {
		effect = domain.EffectAbsent
	}
	return SupplyEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: effect != domain.EffectUnknown, Supply: supply, Allowed: domain.Known(effect == domain.EffectCompleted)}, nil
}
func supplyFixture(t *testing.T) (*fixture, *supplyEnvironment) {
	t.Helper()
	f := newFixture(t)
	supply, _ := domain.NewSupplyAllow("Meal1", "Meal", domain.Cell{X: 3, Z: 4})
	action, _ := domain.NewSupplyAllowAction("allow-1", supply)
	plan, _ := domain.NewPlan("supplies", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &supplyEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}
func TestSupplyUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := supplyFixture(t)
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
	f.executor.journal, f.executor.supplyJournal = f.store, f.store
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
	if err != nil || len(state.SupplyAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
}
func TestSupplyEmergencyAndDirectionChangesBlockDispatch(t *testing.T) {
	for _, changed := range []bool{false, true} {
		t.Run(map[bool]string{false: "unknown emergency", true: "player direction"}[changed], func(t *testing.T) {
			f, n := supplyFixture(t)
			if changed {
				n.onInspect = func() { f.authority.Enabled = false; _ = f.executor.UpdateAuthority(f.authority) }
			} else {
				n.unsafe = true
			}
			if _, err := f.run(); err == nil || n.allowed != 0 {
				t.Fatal("unsafe write", err)
			}
		})
	}
}
func TestSupplyRejectsForeignCompletionAndAbsence(t *testing.T) {
	f, n := supplyFixture(t)
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
func TestSupplyTypedPreparationCannotBeBypassed(t *testing.T) {
	f, _ := supplyFixture(t)
	if _, err := f.store.Prepare(context.Background(), f.plan.ID(), f.action.ID(), f.authority.Snapshot, 100); err == nil {
		t.Fatal("generic preparation bypassed snapshot")
	}
	wrong := store.SupplyAdmission{Snapshot: f.authority.Snapshot, Tick: 100, Thing: "other", SnapshotToken: "token"}
	if _, err := f.store.PrepareSupply(context.Background(), f.plan.ID(), f.action.ID(), wrong); err == nil {
		t.Fatal("foreign item admitted")
	}
}
