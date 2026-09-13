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

type questAcceptEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	effect                          domain.Effect
}

func (n *questAcceptEnvironment) questAcceptFacts(target Target) policy.QuestAcceptFacts {
	accept, _ := target.Action.QuestAccept()
	quest := policy.QuestFacts{
		Quest: accept.Quest(), SnapshotToken: "quest-token", State: domain.Known("NotYetAccepted"),
		RequiresAccepter: domain.Known(true), CanAccept: domain.Known(true), ChoiceCount: domain.Known(int32(1)),
		EligibleAccepters: []domain.PawnID{accept.AccepterPawn()}, HasTradeRequest: domain.Known(false),
	}
	return policy.QuestAcceptFacts{Snapshot: target.Snapshot, QuestTick: n.tick, PreviewTick: n.tick, Quest: quest, NativeCanTry: domain.Known(!n.ineligible)}
}
func (n *questAcceptEnvironment) InspectQuestAccept(_ context.Context, target Target) (QuestAcceptInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return QuestAcceptInspection{StartedAt: now, ObservedAt: now, Facts: n.questAcceptFacts(target)}, nil
}
func (n *questAcceptEnvironment) WriteQuestAccept(_ context.Context, dispatch QuestAcceptDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *questAcceptEnvironment) ObserveQuestAccept(_ context.Context, dispatch QuestAcceptDispatch, current domain.GenerationSnapshot) (QuestAcceptEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	quest := dispatch.Admission.Quest
	if n.foreign {
		quest = "foreign-quest"
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return QuestAcceptEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Quest: quest}, nil
}

func questAcceptFixture(t *testing.T) (*fixture, *questAcceptEnvironment) {
	t.Helper()
	f := newFixture(t)
	accept, _ := domain.NewQuestAccept("quest-1", "pawn-1", 0)
	action, _ := domain.NewQuestAcceptAction("quest-accept-1", accept)
	plan, _ := domain.NewPlan("quest-accepts", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &questAcceptEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableQuestAccept(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestQuestAcceptAdmitsAndDispatches(t *testing.T) {
	f, n := questAcceptFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.QuestAcceptAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
}

func TestQuestAcceptUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := questAcceptFixture(t)
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
	f.executor.journal, f.executor.questAcceptJournal = f.store, f.store
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

func TestQuestAcceptNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := questAcceptFixture(t)
	n.ineligible = true
	result, err := f.run()
	if err != ErrHeld || result.Progress.View().Stage != domain.Pending || n.dispatched != 0 {
		t.Fatal(result, err)
	}
}

func TestQuestAcceptReconcileRejectsForeignQuest(t *testing.T) {
	f, n := questAcceptFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
