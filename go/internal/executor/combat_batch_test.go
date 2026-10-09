package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	"testing"
)

func combatFixture(t *testing.T) *fixture {
	t.Helper()
	b, err := domain.NewCombatBatch("fight", "combat-key", []domain.CombatCommand{{Kind: "draft", Pawn: "a"}, {Kind: "attack", Pawn: "a", Target: "enemy"}, {Kind: "drug", Pawn: "b", Drug: "GoJuice"}})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewCombatBatchAction("combat", b)
	if err != nil {
		t.Fatal(err)
	}
	return newFixtureAt(t, storetest.Path(t), a)
}
func combatResults() []domain.CombatResult {
	return []domain.CombatResult{{Index: 0, PawnID: "a", Applied: true}, {Index: 1, PawnID: "a", Applied: false, Refusal: "cannot_hit"}, {Index: 2, PawnID: "b", Applied: true}}
}
func TestCombatBatchJournalBeforeWriteAndPartialResults(t *testing.T) {
	f := combatFixture(t)
	f.env.onPlace = func(ctx context.Context, p Placement) (Receipt, error) {
		saved, err := f.store.LoadPlan(ctx, f.plan.ID())
		if err != nil {
			t.Fatal(err)
		}
		if saved.Spec.Actions()[0] != p.Action || saved.Progress[0].View().Stage != domain.Dispatched {
			t.Fatal("write before exact dispatch journal", saved)
		}
		return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted, Combat: combatResults()}, nil
	}
	for range 2 {
		items, err := f.executor.RunBatch(context.Background(), f.plan.ID(), []domain.ActionID{f.action.ID()})
		if err != nil || items[0].Err != nil {
			t.Fatal(err, items)
		}
	}
	v := f.progress(t)
	results, ok := v.Combat.Value()
	if !ok || len(results.Orders()) != 3 || results.Orders()[1].Applied || v.Unresolved || f.env.writes != 1 {
		t.Fatal(v, f.env.writes)
	}
}

type failCombatJournal struct {
	Journal
	beforeDispatch, beforeReceipt bool
}

func (j failCombatJournal) DispatchBatch(ctx context.Context, a []store.BatchAttempt) ([]store.BatchResult, error) {
	if j.beforeDispatch {
		return nil, errors.New("crash before dispatch")
	}
	return j.Journal.DispatchBatch(ctx, a)
}
func (j failCombatJournal) RecordReceipts(ctx context.Context, r []store.BatchReceipt) ([]store.BatchResult, error) {
	if j.beforeReceipt {
		return nil, errors.New("crash before receipt commit")
	}
	return j.Journal.RecordReceipts(ctx, r)
}
func TestCombatFaultsNeverReplayUncertainEffects(t *testing.T) {
	for _, fault := range []string{"before-dispatch", "lost-reply", "before-evidence", "malformed-receipt", "authority"} {
		t.Run(fault, func(t *testing.T) {
			f := combatFixture(t)
			f.executor.journal = failCombatJournal{Journal: f.store, beforeDispatch: fault == "before-dispatch", beforeReceipt: fault == "before-evidence"}
			f.env.onPlace = func(ctx context.Context, p Placement) (Receipt, error) {
				if fault == "lost-reply" {
					return Receipt{}, errors.New("reply lost after apply")
				}
				if fault == "malformed-receipt" {
					return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted, Combat: combatResults()[:1]}, nil
				}
				return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted, Combat: combatResults()}, nil
			}
			if fault == "authority" {
				f.env.onInspect = func(_ int, in IntentInspection) IntentInspection {
					_ = f.executor.UpdateAuthority(Authority{})
					return in
				}
			}
			_, _ = f.executor.RunBatch(context.Background(), f.plan.ID(), []domain.ActionID{f.action.ID()})
			v := f.progress(t)
			if fault == "before-dispatch" || fault == "authority" {
				if f.env.writes != 0 {
					t.Fatal("wrote without authority/dispatch")
				}
				return
			}
			if !v.Unresolved || v.Stage != domain.Dispatched && v.Stage != domain.AwaitingObservation {
				t.Fatal("uncertainty erased", v)
			}
			f.executor.journal = f.store
			_, _ = f.executor.RunBatch(context.Background(), f.plan.ID(), []domain.ActionID{f.action.ID()})
			if f.env.writes != 1 {
				t.Fatal("replayed uncertain attack/drug", f.env.writes)
			}
			changed := f.authority
			changed.Snapshot.Load = "reloaded"
			_ = f.executor.UpdateAuthority(changed)
			_, _ = f.executor.RunBatch(context.Background(), f.plan.ID(), []domain.ActionID{f.action.ID()})
			if f.env.writes != 1 {
				t.Fatal("old-session replay")
			}
		})
	}
}
