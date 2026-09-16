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

// excavationEnvironment mirrors mineAcquisitionEnvironment for the staged
// excavation vertical: same inspect/admit/dispatch/reconcile machine through
// its own boundary/journal wiring, with cell-cleared completion evidence.
type excavationEnvironment struct {
	*environment
	inspected, allowed, observed int
	uncertain, unsafe, foreign   bool
	absent                       bool
	fogged, unsupported, moved   bool
	noWorker                     bool
	onInspect                    func()
	effect                       domain.Effect
}

func (n *excavationEnvironment) InspectExcavation(_ context.Context, target Target) (ExcavationInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	excavation, _ := target.Action.Excavation()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true)})
	facts := policy.ExcavationFacts{Snapshot: target.Snapshot, ObservationTick: n.tick, Fogged: domain.Known(n.fogged), Support: policy.ExcavationSupportSupported, WorkerAvailable: domain.Known(!n.noWorker), AccessReachable: domain.Known(true)}
	if !n.fogged {
		facts.Definition, facts.Eligible = domain.Known(excavation.Definition()), domain.Known(true)
	}
	if n.moved {
		facts.Definition = domain.Known("Marble")
	}
	if n.unsupported {
		facts.Support = policy.ExcavationSupportUnsupported
	}
	token := "excavate-token"
	if n.fogged || n.unsupported || n.moved {
		token = ""
	}
	return ExcavationInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Excavation: excavation, SnapshotToken: token, Facts: facts, Emergency: emergency}, nil
}
func (n *excavationEnvironment) Excavate(_ context.Context, request ExcavationDispatch) (Receipt, error) {
	n.allowed++
	if request.SnapshotToken != "excavate-token" {
		return Receipt{}, ErrEvidence
	}
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *excavationEnvironment) ObserveExcavation(_ context.Context, p Placement, current domain.GenerationSnapshot) (ExcavationEvidence, error) {
	n.observed++
	excavation, _ := p.Action.Excavation()
	if n.foreign {
		excavation, _ = domain.NewExcavation(excavation.Cell(), "Marble")
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	if n.absent {
		effect = domain.EffectAbsent
	}
	out := ExcavationEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: effect != domain.EffectUnknown, Excavation: excavation}
	switch effect {
	case domain.EffectCompleted:
		out.Cleared = true
	case domain.EffectUnsuccessful:
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
		out.Blocker = "Removal would leave unsupported roof"
	case domain.EffectPending:
		out.Designated = true
	}
	return out, nil
}
func excavationFixture(t *testing.T) (*fixture, *excavationEnvironment) {
	t.Helper()
	f := newFixture(t)
	excavation, _ := domain.NewExcavation(domain.Cell{X: 3, Z: 4}, "Granite")
	action, _ := domain.NewExcavationAction("dig-1", excavation)
	plan, _ := domain.NewPlan("excavating", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &excavationEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableExcavation(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}
func TestExcavationDispatchesThenCompletesOnClearedCell(t *testing.T) {
	f, n := excavationFixture(t)
	result, err := f.run()
	if err != nil || n.allowed != 1 || n.inspected != 2 || result.Progress.View().Attempt != 1 {
		t.Fatal(result, err, n)
	}
	n.effect = domain.EffectPending
	if result, err = f.run(); err != nil || result.Progress.View().Stage == domain.Completed {
		t.Fatal(result, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.allowed != 1 {
		t.Fatal(result, err)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.ExcavationAdmissions) != 1 || state.ExcavationAdmissions[0].Admission.Cell != (domain.Cell{X: 3, Z: 4}) {
		t.Fatal(state, err)
	}
}
func TestExcavationRejectsForeignCompletionAndAbsence(t *testing.T) {
	f, n := excavationFixture(t)
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
func TestExcavationGuardEndedJobIsUnsuccessful(t *testing.T) {
	f, n := excavationFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectUnsuccessful
	result, err := f.run()
	if err != nil || result.Progress.View().Stage != domain.Unsuccessful || n.allowed != 1 {
		t.Fatal(result, err)
	}
}
func TestExcavationHoldsOnFogUnsupportedGeometryAndWorkers(t *testing.T) {
	cases := []struct {
		name   string
		set    func(*excavationEnvironment)
		reason domain.HeldReason
	}{
		{"fogged", func(n *excavationEnvironment) { n.fogged = true }, domain.HeldUnknownFacts},
		{"unsupported", func(n *excavationEnvironment) { n.unsupported = true }, domain.HeldExcavationUnsupported},
		{"geometry", func(n *excavationEnvironment) { n.moved = true }, domain.HeldExcavationGeometryChanged},
		{"no worker", func(n *excavationEnvironment) { n.noWorker = true }, domain.HeldNotReady},
		{"emergency", func(n *excavationEnvironment) { n.unsafe = true }, domain.HeldUnknownFacts},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			f, n := excavationFixture(t)
			c.set(n)
			if _, err := f.run(); !errors.Is(err, ErrHeld) || n.allowed != 0 {
				t.Fatal("unsafe write", err)
			}
			held, ok := f.progress(t).FreshHeldReason()
			if !ok || len(held) != 1 || held[0] != c.reason {
				t.Fatal("hold was not persisted as the expected reason", held)
			}
		})
	}
}
func TestExcavationUnknownReplyReopensAndObservesAfterRestart(t *testing.T) {
	f, n := excavationFixture(t)
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
	f.executor.journal, f.executor.excavationJournal = f.store, f.store
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
}
func TestExcavationTypedPreparationCannotBeBypassed(t *testing.T) {
	f, _ := excavationFixture(t)
	if _, err := f.store.Prepare(context.Background(), f.plan.ID(), f.action.ID(), f.authority.Snapshot, 100); err == nil {
		t.Fatal("generic preparation bypassed snapshot")
	}
	wrong := store.ExcavationAdmission{Snapshot: f.authority.Snapshot, Tick: 100, Cell: domain.Cell{X: 9, Z: 9}, Definition: "Granite", SnapshotToken: "token"}
	if _, err := f.store.PrepareExcavation(context.Background(), f.plan.ID(), f.action.ID(), wrong); err == nil {
		t.Fatal("foreign cell admitted")
	}
}
