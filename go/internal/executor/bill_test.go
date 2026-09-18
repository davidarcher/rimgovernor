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

type billEnvironment struct {
	*environment
	inspected, added, observed     int
	onInspect                      func()
	onObserve                      func(*BillEvidence)
	attemptOutcome                 func(Receipt, error) (Receipt, error)
	effect                         domain.Effect
	matches                        bool
	unsafe                         bool
	iterations                     uint32
	outputComplete, outputObserved bool
	outputCount                    int
}

func (n *billEnvironment) InspectBill(_ context.Context, target Target) (BillInspection, error) {
	n.inspected++
	if n.onInspect != nil {
		n.onInspect()
	}
	bill, _ := target.Action.ProductionBill()
	tick := domain.Tick(100 + int64(n.inspected))
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true)})
	return BillInspection{Current: target.Snapshot, Tick: tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Bill: bill, SnapshotToken: bill.BeforeToken(), Accepted: true, Emergency: emergency}, nil
}
func (n *billEnvironment) AddBill(_ context.Context, request BillDispatch) (Receipt, error) {
	n.added++
	bill, _ := request.Attempt.Action.ProductionBill()
	if request.SnapshotToken != bill.BeforeToken() {
		return Receipt{}, ErrEvidence
	}
	p := request.Attempt
	receipt := Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}
	if n.attemptOutcome != nil {
		return n.attemptOutcome(receipt, nil)
	}
	return receipt, nil
}
func (n *billEnvironment) ObserveBill(_ context.Context, p Placement, current domain.GenerationSnapshot) (BillEvidence, error) {
	n.observed++
	bill, _ := p.Action.ProductionBill()
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	complete := effect == domain.EffectCompleted || effect == domain.EffectUnsuccessful || effect == domain.EffectAbsent
	e := BillEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: domain.AfterDispatch}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: complete, Bill: bill}
	if effect == domain.EffectCompleted || effect == domain.EffectUnsuccessful {
		e.Matches = domain.Known(n.matches)
		e.Iterations = n.iterations
		e.OutputComplete = n.outputComplete
		e.OutputObserved = n.outputObserved
		e.OutputCount = n.outputCount
	}
	if effect == domain.EffectUnsuccessful {
		e.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
	}
	if n.onObserve != nil {
		n.onObserve(&e)
	}
	return e, nil
}
func billFixture(t *testing.T) (*fixture, *billEnvironment) {
	t.Helper()
	f := newFixture(t)
	bill, err := domain.NewProductionBill("bench", "recipe", "bench-cas", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("bill-1", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("bills", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &billEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableBill(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestBillEmergencyBlocksDispatch(t *testing.T) {
	f, n := billFixture(t)
	n.unsafe = true
	if _, err := f.run(); err == nil || n.added != 0 {
		t.Fatal("unsafe write", err)
	}
	held, ok := f.progress(t).FreshHeldReason()
	if !ok || len(held) != 1 || held[0] != domain.HeldUnknownFacts {
		t.Fatal("emergency hold was not persisted as a held reason", held)
	}
}

// A destroyed bench (or a bill row removed from it) must not strand the goal:
// once the native side reports the bill absent, the progress must resolve and
// return to Pending so a fresh bill can be prepared and dispatched, instead of
// retrying ErrEvidence forever.
func TestBillObserveAbsentReopensForFreshDispatch(t *testing.T) {
	f, n := billFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectAbsent
	result, err := f.run()
	if err != nil {
		t.Fatal(err)
	}
	if result.Progress.View().Unresolved {
		t.Fatal("absence left the bill unresolved", result)
	}
	if result.Progress.View().Stage != domain.Pending {
		t.Fatal("absence did not return the bill to pending", result)
	}
	if n.added != 1 {
		t.Fatal("first dispatch did not add the bill", n)
	}
	result, err = f.run()
	if err != nil {
		t.Fatal(err)
	}
	if n.added != 2 || n.inspected != 4 {
		t.Fatal("reopened bill was not redispatched", result, n)
	}
}

// Absence is only trustworthy when the native inspection was complete;
// a partial read reporting absence must still be refused as bad evidence
// and leave the bill retrying, not resolved.
func TestBillObserveIncompleteAbsentRefused(t *testing.T) {
	f, n := billFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectAbsent
	n.onObserve = func(e *BillEvidence) { e.Complete = false }
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal(err)
	}
	if !f.progress(t).Unresolved {
		t.Fatal("incomplete absence resolved the bill", f.progress(t))
	}
}

// A stale observation naming a different bench+recipe than the dispatched
// one must not be accepted as this bill's absence.
func TestBillObserveForeignBillAbsentRefused(t *testing.T) {
	f, n := billFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	foreign, err := domain.NewProductionBill("other-bench", "recipe", "bench-cas", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectAbsent
	n.onObserve = func(e *BillEvidence) { e.Bill = foreign }
	if _, err = f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal(err)
	}
	if !f.progress(t).Unresolved {
		t.Fatal("foreign bill absence resolved the bill", f.progress(t))
	}
}

func TestBillRejectsUnrecognizedEffect(t *testing.T) {
	f, n := billFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect = "bogus"
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal(err)
	}
	if !f.progress(t).Unresolved {
		t.Fatal("bad evidence released uncertainty")
	}
}

// A setup receipt alone must never authorize completion: every one of the
// output-correlation fields (configuration match, iterations, and both output
// completeness signals with a nonzero count) must be independently required,
// or an unobserved/uncounted output could be mistaken for real production.
func TestBillCompletedRequiresFullOutputCorrelation(t *testing.T) {
	base := func(n *billEnvironment) {
		n.effect = domain.EffectCompleted
		n.matches, n.iterations, n.outputComplete, n.outputObserved, n.outputCount = true, 1, true, true, 1
	}
	cases := map[string]func(*billEnvironment){
		"config mismatch":   func(n *billEnvironment) { base(n); n.matches = false },
		"zero iterations":   func(n *billEnvironment) { base(n); n.iterations = 0 },
		"output incomplete": func(n *billEnvironment) { base(n); n.outputComplete = false },
		"output unobserved": func(n *billEnvironment) { base(n); n.outputObserved = false },
		"zero output count": func(n *billEnvironment) { base(n); n.outputCount = 0 },
	}
	for name, mutate := range cases {
		t.Run(name, func(t *testing.T) {
			f, n := billFixture(t)
			if _, err := f.run(); err != nil {
				t.Fatal(err)
			}
			mutate(n)
			if _, err := f.run(); !errors.Is(err, ErrEvidence) {
				t.Fatal(err)
			}
			if !f.progress(t).Unresolved {
				t.Fatal("partial output correlation completed the bill")
			}
		})
	}
	f, n := billFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	base(n)
	result, err := f.run()
	if err != nil {
		t.Fatal(err)
	}
	if result.Progress.View().Stage != domain.Completed {
		t.Fatal("fully correlated output did not complete the bill", result)
	}
}

// A lost reply (native accepted the write but the reply never arrived) must
// survive a full executor restart: rebuilding from the durable journal alone,
// the executor must reconcile the earlier dispatch rather than adding the
// bill a second time.
func TestBillLostReplySurvivesRestart(t *testing.T) {
	f, n := billFixture(t)
	n.attemptOutcome = func(Receipt, error) (Receipt, error) { return Receipt{}, errors.New("reply lost") }
	result, err := f.run()
	if err == nil || !result.Progress.View().Unresolved || n.added != 1 {
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
	n.attemptOutcome = nil
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableBill(n); err != nil {
		t.Fatal(err)
	}
	f.executor = e
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectCompleted
	n.matches, n.iterations, n.outputComplete, n.outputObserved, n.outputCount = true, 1, true, true, 1
	result, err = f.run()
	if err != nil {
		t.Fatal(err)
	}
	if n.added != 1 {
		t.Fatal("restart re-added a bill whose write may already have landed", n)
	}
	if result.Progress.View().Stage != domain.Completed {
		t.Fatal("restart did not reconcile the earlier dispatch", result)
	}
}

// See TestSupplyUnadmittedAttemptRetries: a dispatch the ledger never
// admitted (no receipt, no bill) returns to Pending and is added again
// under a fresh attempt (#165).
func TestBillUnadmittedAttemptRetries(t *testing.T) {
	f, n := billFixture(t)
	n.attemptOutcome = func(Receipt, error) (Receipt, error) { return Receipt{}, errors.New("reply lost") }
	if _, err := f.run(); err == nil || n.added != 1 {
		t.Fatal(err, n.added)
	}
	n.attemptOutcome, n.effect = nil, domain.EffectAbsent
	result, err := f.run()
	if err != nil || result.Progress.View().Unresolved || result.Progress.View().Stage != domain.Pending || n.added != 1 {
		t.Fatal(result, err, n.added)
	}
	n.effect = ""
	result, err = f.run()
	if err != nil || n.added != 2 || result.Progress.View().Attempt != 2 {
		t.Fatal(result, err, n.added)
	}
	n.effect, n.matches, n.iterations, n.outputComplete, n.outputObserved, n.outputCount = domain.EffectCompleted, true, 1, true, true, 1
	if result, err = f.run(); err != nil || result.Progress.View().Stage != domain.Completed {
		t.Fatal(result, err)
	}
}
