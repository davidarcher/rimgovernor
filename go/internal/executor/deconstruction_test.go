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

type deconstructionEnvironment struct {
	*environment
	inspected, allowed, observed       int
	uncertain, unsafe, foreign, absent bool
	unadmitted                         bool
	onInspect                          func()
	gone                               bool
	effect                             domain.Effect
	// threat adds a live hostile to the emergency read; refusals make the
	// target ineligible with those reasons (#525).
	threat   bool
	refusals []policy.Refusal
}

func (n *deconstructionEnvironment) InspectDeconstruction(_ context.Context, target Target) (DeconstructionInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	if n.gone {
		return DeconstructionInspection{}, ErrDeconstructionAbsent
	}
	value, _ := target.Action.Deconstruction()
	facts := policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true)}
	if n.threat {
		facts.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), Distance: domain.Known(150.0)}}
	}
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, facts)
	eligible := len(n.refusals) == 0
	return DeconstructionInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Target: value, Eligible: eligible, Accepted: eligible, Emergency: emergency, Refusals: n.refusals}, nil
}
func (n *deconstructionEnvironment) DesignateDeconstruction(_ context.Context, request DeconstructionDispatch) (Receipt, error) {
	n.allowed++
	if !request.Eligible {
		return Receipt{}, ErrEvidence
	}
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *deconstructionEnvironment) ObserveDeconstruction(_ context.Context, p Placement, current domain.GenerationSnapshot) (DeconstructionEvidence, error) {
	n.observed++
	target, _ := p.Action.Deconstruction()
	if n.foreign {
		target, _ = domain.NewDeconstruction("foreign", target.Definition(), target.Cell())
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
	return DeconstructionEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causality}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: effect != domain.EffectUnknown, Target: target, Demolished: domain.Known(effect == domain.EffectCompleted || effect == domain.EffectPending)}, nil
}
func deconstructionFixture(t *testing.T) (*fixture, *deconstructionEnvironment) {
	t.Helper()
	f := newFixture(t)
	target, _ := domain.NewDeconstruction("ruin1", "Wall", domain.Cell{X: 3, Z: 4})
	action, _ := domain.NewDeconstructionAction("cut-1", target)
	plan, _ := domain.NewPlan("clearance", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &deconstructionEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableDeconstruction(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}
func TestDeconstructionUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := deconstructionFixture(t)
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
	f.executor.journal, f.executor.deconstructionJournal = f.store, f.store
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
	if err != nil || len(state.DeconstructionAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
}
func TestDeconstructionEmergencyAndDirectionChangesBlockDispatch(t *testing.T) {
	for _, changed := range []bool{false, true} {
		t.Run(map[bool]string{false: "unknown emergency", true: "player direction"}[changed], func(t *testing.T) {
			f, n := deconstructionFixture(t)
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

func TestDeconstructionAbsentBeforeDispatchCancels(t *testing.T) {
	f, n := deconstructionFixture(t)
	n.gone = true
	if _, err := f.run(); !errors.Is(err, ErrHeld) || n.allowed != 0 {
		t.Fatal(err, n.allowed)
	}
	if stage := f.progress(t).Stage; stage != domain.Cancelled {
		t.Fatal("absent target left the action open", stage)
	}
}
func TestDeconstructionRejectsForeignCompletionAndAbsence(t *testing.T) {
	f, n := deconstructionFixture(t)
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

func TestDeconstructionUnadmittedAttemptRetries(t *testing.T) {
	f, n := deconstructionFixture(t)
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
func TestDeconstructionTypedPreparationCannotBeBypassed(t *testing.T) {
	f, _ := deconstructionFixture(t)
	if _, err := f.store.Prepare(context.Background(), f.plan.ID(), f.action.ID(), f.authority.Snapshot, 100); err == nil {
		t.Fatal("generic preparation bypassed snapshot")
	}
	wrong := store.DeconstructionAdmission{Snapshot: f.authority.Snapshot, Tick: 100, Thing: "other", Eligible: true}
	if _, err := f.store.PrepareDeconstruction(context.Background(), f.plan.ID(), f.action.ID(), wrong); err == nil {
		t.Fatal("foreign item admitted")
	}
}

func TestDeconstructionPendingStaysOpenUntilDemolitionObserved(t *testing.T) {
	f, n := deconstructionFixture(t)
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

// A remote designation is revalidated at every dispatch (#525): a hostile
// that appears after selection, a roof the removal would drop, a route that
// turned unsafe or storage that filled each hold the pending action with the
// reason on record and never reach native. Once the holds clear it is
// designated exactly once, and a restart on the same journal observes the
// in-flight designation instead of placing a second one.
func TestDeconstructionRemoteHoldsRecordReasonsAndResumeWithoutDuplicates(t *testing.T) {
	f, n := deconstructionFixture(t)
	expect := func(want ...domain.HeldReason) {
		t.Helper()
		if _, err := f.run(); !errors.Is(err, ErrHeld) || n.allowed != 0 {
			t.Fatal("dispatched under a hold", err, n.allowed)
		}
		held, ok := f.progress(t).FreshHeldReason()
		if !ok || len(held) != len(want) {
			t.Fatal("hold reasons", held, want)
		}
		for i := range want {
			if held[i] != want[i] {
				t.Fatal("hold reasons", held, want)
			}
		}
	}
	n.threat = true
	expect(domain.HeldUnsafeThreat)
	n.refusals = []policy.Refusal{{Action: f.action.ID(), Reason: policy.UnsafeRoute}}
	expect(domain.HeldUnsafeThreat, domain.HeldUnsafeRoute)
	n.threat = false
	for _, reason := range []policy.Reason{policy.RoofSupportRisk, policy.StorageMissing, policy.UrgentCompetingWork, policy.StructureIneligible} {
		n.refusals = []policy.Refusal{{Action: f.action.ID(), Reason: reason}}
		expect(reasonHeldReasons[reason])
	}
	n.refusals = nil
	result, err := f.run()
	if err != nil || n.allowed != 1 || !result.Progress.View().Unresolved {
		t.Fatal("hold did not clear into one designation", result, err, n.allowed)
	}
	if _, ok := f.progress(t).FreshHeldReason(); ok {
		t.Fatal("dispatch left a stale hold reason")
	}
	// Restart: a fresh executor over the same journal.
	if err = f.store.Close(); err != nil {
		t.Fatal(err)
	}
	f.store, err = store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.store.Close()
	restarted, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = restarted.EnableDeconstruction(n); err != nil {
		t.Fatal(err)
	}
	if err = restarted.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	f.executor = restarted
	n.effect = domain.EffectPending
	if _, err = f.run(); err != nil || n.allowed != 1 || n.observed != 1 {
		t.Fatal("restart re-designated instead of observing", err, n.allowed, n.observed)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.allowed != 1 {
		t.Fatal(result, err, n.allowed)
	}
}
