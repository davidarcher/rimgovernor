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

type acquisitionEnvironment struct {
	*environment
	inspected, allowed, observed       int
	withdrawn                          int
	uncertain, unsafe, foreign, absent bool
	// unadmitted marks the absent evidence as the boundary's complete
	// post-dispatch ledger lookup, as boundary.Unadmitted reports it.
	unadmitted bool
	onInspect  func()
	effect     domain.Effect
}

func (n *acquisitionEnvironment) InspectAcquisition(_ context.Context, target Target) (AcquisitionInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	acquisition, _ := target.Action.Acquisition()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true)})
	return AcquisitionInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Acquisition: acquisition, SnapshotToken: "acquisition-token", Accepted: true, Emergency: emergency}, nil
}
func (n *acquisitionEnvironment) Acquire(_ context.Context, request AcquisitionDispatch) (Receipt, error) {
	n.allowed++
	if request.SnapshotToken != "acquisition-token" {
		return Receipt{}, ErrEvidence
	}
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *acquisitionEnvironment) WithdrawAcquisition(_ context.Context, request AcquisitionDispatch) (Receipt, error) {
	n.withdrawn++
	if request.SnapshotToken != "" {
		return Receipt{}, ErrEvidence
	}
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *acquisitionEnvironment) ObserveAcquisition(_ context.Context, p Placement, current domain.GenerationSnapshot) (AcquisitionEvidence, error) {
	n.observed++
	acquisition, _ := p.Action.Acquisition()
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
	var causality domain.ObservationCausality
	if n.unadmitted {
		causality = domain.AfterDispatch
	}
	// A withdrawn designation fails without labor (designated=false); an
	// unsuccessful harvest otherwise finished its labor with nothing.
	labor := effect == domain.EffectCompleted || effect == domain.EffectUnsuccessful && n.withdrawn == 0
	units := int32(10)
	var reason domain.UnsuccessfulReason
	if effect == domain.EffectUnsuccessful {
		units, reason = 0, domain.OutcomeNotAchieved
	}
	return AcquisitionEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causality, UnsuccessfulReason: reason}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: effect != domain.EffectUnknown, Acquisition: acquisition, LaborFinished: labor, OutputComplete: true, OutputObserved: effect == domain.EffectCompleted, ProducedUnits: units, Designated: effect == domain.EffectPending && n.withdrawn == 0, PendingReason: "2 enabled plant cutter(s) busy"}, nil
}
func acquisitionFixture(t *testing.T) (*fixture, *acquisitionEnvironment) {
	t.Helper()
	f := newFixture(t)
	acquisition, _ := domain.NewAcquisition("Meal1", "Meal", domain.Cell{X: 3, Z: 4})
	action, _ := domain.NewAcquisitionAction("allow-1", acquisition)
	plan, _ := domain.NewPlan("supplies", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &acquisitionEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableAcquisition(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}
func TestAcquisitionUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := acquisitionFixture(t)
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
	f.executor.journal, f.executor.acquisitionJournal = f.store, f.store
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
	if err != nil || len(state.AcquisitionAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
}
func TestAcquisitionEmergencyAndDirectionChangesBlockDispatch(t *testing.T) {
	for _, changed := range []bool{false, true} {
		t.Run(map[bool]string{false: "unknown emergency", true: "player direction"}[changed], func(t *testing.T) {
			f, n := acquisitionFixture(t)
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
func TestAcquisitionRejectsForeignCompletionAndAbsence(t *testing.T) {
	f, n := acquisitionFixture(t)
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
func TestAcquisitionTypedPreparationCannotBeBypassed(t *testing.T) {
	f, _ := acquisitionFixture(t)
	if _, err := f.store.Prepare(context.Background(), f.plan.ID(), f.action.ID(), f.authority.Snapshot, 100); err == nil {
		t.Fatal("generic preparation bypassed snapshot")
	}
	wrong := store.AcquisitionAdmission{Snapshot: f.authority.Snapshot, Tick: 100, Thing: "other", SnapshotToken: "token"}
	if _, err := f.store.PrepareAcquisition(context.Background(), f.plan.ID(), f.action.ID(), wrong); err == nil {
		t.Fatal("foreign item admitted")
	}
}

// A dispatch that timed out before the native ledger admitted it leaves an
// unknown receipt; the ledger lookup then proves no attempt exists, and the
// action returns to Pending and is dispatched again under a fresh attempt
// instead of awaiting an observation that can never arrive (#165).
func TestAcquisitionUnadmittedAttemptRetries(t *testing.T) {
	f, n := acquisitionFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil || n.allowed != 1 {
		t.Fatal(err, n.allowed)
	}
	n.uncertain, n.absent, n.unadmitted = false, true, true
	result, err := f.run()
	if err != nil || result.Progress.View().Unresolved || result.Progress.View().Stage != domain.Pending || n.allowed != 1 {
		t.Fatal(result, err, n.allowed)
	}
	n.absent, n.unadmitted = false, false
	n.tick += 10
	result, err = f.run()
	if err != nil || n.allowed != 2 || result.Progress.View().Attempt != 2 {
		t.Fatal(result, err, n.allowed)
	}
	n.effect = domain.EffectCompleted
	if result, err = f.run(); err != nil || result.Progress.View().Stage != domain.Completed {
		t.Fatal(result, err)
	}
}

// A cancelled acquisition whose designation still sits untaken (#291) is
// withdrawn natively under a fresh attempt, and the withdrawn record's
// unsuccessful effect (designation gone, no labor) settles the action.
func TestAcquisitionCancelledPendingDesignationIsWithdrawn(t *testing.T) {
	f, n := acquisitionFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectPending
	result, err := f.run()
	if err != nil || result.Detail != "2 enabled plant cutter(s) busy" || n.withdrawn != 0 || result.Progress.View().Stage != domain.AwaitingObservation {
		t.Fatal("pending designation reported without its reason", result, err)
	}
	if _, err = f.store.Cancel(context.Background(), f.plan.ID(), f.action.ID()); err != nil {
		t.Fatal(err)
	}
	result, err = f.run()
	v := result.Progress.View()
	if err != nil || n.withdrawn != 1 || !result.NativeCalled || v.Stage != domain.Cancelled || !v.Unresolved || v.Attempt != 2 {
		t.Fatalf("cancelled pending designation not withdrawn: %+v %v %+v", v, err, n)
	}
	if receipt, known := v.Receipt.Value(); !known || receipt != domain.ReceiptAccepted {
		t.Fatal("withdrawal receipt not journaled", v)
	}
	n.effect = domain.EffectUnsuccessful
	result, err = f.run()
	v = result.Progress.View()
	if err != nil || v.Unresolved || v.Stage != domain.Cancelled || n.withdrawn != 1 || n.allowed != 1 {
		t.Fatalf("withdrawn designation did not settle: %+v %v %+v", v, err, n)
	}
	if domain.GoalWorkOpen([]domain.Progress{result.Progress}) {
		t.Fatal("settled withdrawal still holds the goal's work open")
	}
}

// Without a live designation there is nothing to withdraw: a cancelled
// action whose effect is pending but undesignated keeps observing.
func TestAcquisitionCancelledUndesignatedPendingIsNotWithdrawn(t *testing.T) {
	f, n := acquisitionFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Cancel(context.Background(), f.plan.ID(), f.action.ID()); err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectUnsuccessful
	result, err := f.run()
	if err != nil || n.withdrawn != 0 || result.Progress.View().Unresolved {
		t.Fatal("labor-finished failure of a cancelled action not settled", result, err, n)
	}
}
