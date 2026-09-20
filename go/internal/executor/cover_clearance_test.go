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

type coverClearanceEnvironment struct {
	*environment
	inspected, allowed, observed       int
	uncertain, unsafe, foreign, absent bool
	// unadmitted marks the absent evidence as the boundary's complete
	// post-dispatch ledger lookup, as boundary.Unadmitted reports it.
	unadmitted bool
	onInspect  func()
	gone       bool
	effect     domain.Effect
}

func (n *coverClearanceEnvironment) InspectCoverClearance(_ context.Context, target Target) (CoverClearanceInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	if n.gone {
		return CoverClearanceInspection{}, ErrCoverClearanceAbsent
	}
	clearance, _ := target.Action.CoverClearance()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true)})
	return CoverClearanceInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Clearance: clearance, SnapshotToken: "cover-token", Accepted: true, Emergency: emergency}, nil
}
func (n *coverClearanceEnvironment) DesignateCoverClearance(_ context.Context, request CoverClearanceDispatch) (Receipt, error) {
	n.allowed++
	if request.SnapshotToken != "cover-token" {
		return Receipt{}, ErrEvidence
	}
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *coverClearanceEnvironment) ObserveCoverClearance(_ context.Context, p Placement, current domain.GenerationSnapshot) (CoverClearanceEvidence, error) {
	n.observed++
	clearance, _ := p.Action.CoverClearance()
	if n.foreign {
		clearance, _ = domain.NewCoverClearance("foreign", clearance.Definition(), clearance.Designation(), clearance.Cell())
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
	return CoverClearanceEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causality}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: effect != domain.EffectUnknown, Clearance: clearance, Designated: domain.Known(effect == domain.EffectCompleted || effect == domain.EffectPending)}, nil
}
func coverClearanceFixture(t *testing.T) (*fixture, *coverClearanceEnvironment) {
	t.Helper()
	f := newFixture(t)
	clearance, _ := domain.NewCoverClearance("Plant_TreeOak1", "Plant_TreeOak", domain.CoverClearanceCutPlant, domain.Cell{X: 3, Z: 4})
	action, _ := domain.NewCoverClearanceAction("cover-1", clearance)
	plan, _ := domain.NewPlan("cover", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &coverClearanceEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableCoverClearance(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}
func TestCoverClearanceUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := coverClearanceFixture(t)
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
	f.executor.journal, f.executor.coverClearanceJournal = f.store, f.store
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
	if err != nil || len(state.CoverClearanceAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
}
func TestCoverClearanceEmergencyAndDirectionChangesBlockDispatch(t *testing.T) {
	for _, changed := range []bool{false, true} {
		t.Run(map[bool]string{false: "unknown emergency", true: "player direction"}[changed], func(t *testing.T) {
			f, n := coverClearanceFixture(t)
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

// A plant no longer an undesignated blighted plant at its cell before
// dispatch (cut, died, designated by the player) is cancelled so the plan
// can close, never held or designated blind.
func TestCoverClearanceAbsentBeforeDispatchCancels(t *testing.T) {
	f, n := coverClearanceFixture(t)
	n.gone = true
	if _, err := f.run(); !errors.Is(err, ErrHeld) || n.allowed != 0 {
		t.Fatal(err, n.allowed)
	}
	if stage := f.progress(t).Stage; stage != domain.Cancelled {
		t.Fatal("absent target left the action open", stage)
	}
}
func TestCoverClearanceRejectsForeignCompletionAndAbsence(t *testing.T) {
	f, n := coverClearanceFixture(t)
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

// A dispatch that timed out before the native ledger admitted it leaves an
// unknown receipt; the ledger lookup then proves no attempt exists, and the
// action returns to Pending and is designated again under a fresh token.
func TestCoverClearanceUnadmittedAttemptRetries(t *testing.T) {
	f, n := coverClearanceFixture(t)
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
func TestCoverClearanceTypedPreparationCannotBeBypassed(t *testing.T) {
	f, _ := coverClearanceFixture(t)
	if _, err := f.store.Prepare(context.Background(), f.plan.ID(), f.action.ID(), f.authority.Snapshot, 100); err == nil {
		t.Fatal("generic preparation bypassed snapshot")
	}
	wrong := store.CoverClearanceAdmission{Snapshot: f.authority.Snapshot, Tick: 100, Thing: "other", SnapshotToken: "token"}
	if _, err := f.store.PrepareCoverClearance(context.Background(), f.plan.ID(), f.action.ID(), wrong); err == nil {
		t.Fatal("foreign item admitted")
	}
}

// A designated thing still standing is pending work, not a settled attempt;
// the executor keeps observing until the thing is gone.
func TestCoverClearancePendingStaysOpenUntilTheThingIsGone(t *testing.T) {
	f, n := coverClearanceFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectPending
	if result, err := f.run(); err != nil || !result.Progress.View().Unresolved || n.allowed != 1 {
		t.Fatal(result, err, n.allowed)
	}
	n.effect = domain.EffectCompleted
	if result, err := f.run(); err != nil || result.Progress.View().Stage != domain.Completed || n.allowed != 1 {
		t.Fatal(result, err)
	}
}
