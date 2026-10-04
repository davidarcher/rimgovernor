package executor

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func init() {
	domain.RegisterIntentKind(domain.MovementAction)
	domain.RegisterIntentKind(domain.OwnedDraftAction)
}

type movementFake struct {
	writes int
	kind   domain.Receipt
	err    error
	last   Placement
}

func (m *movementFake) WriteMovement(_ context.Context, p Placement) (Receipt, error) {
	m.writes++
	m.last = p
	kind := m.kind
	if kind == "" {
		kind = domain.ReceiptAccepted
	}
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: kind}, m.err
}

func newMovementFixture(t *testing.T) (*fixture, *movementFake) {
	t.Helper()
	f := newFixture(t)
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft-action", draft)
	move, _ := domain.NewMovement("pawn", domain.Cell{X: 3, Z: 4}, d.ID())
	a, _ := domain.NewMovementAction("move-action", move)
	plan, err := domain.NewPlan("movement-plan", 1, []domain.Action{d, a})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	f.plan, f.action = plan, a
	f.authority.Snapshot.Plan = plan.ID()
	mf := &movementFake{}
	s := f.authority.Snapshot
	ctx := context.Background()
	if _, err = f.store.Prepare(ctx, plan.ID(), d.ID(), s, 101); err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.Dispatch(ctx, plan.ID(), d.ID(), s, 101); err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.RecordReceipt(ctx, plan.ID(), d.ID(), 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	f.executor, err = NewWithMovement(f.store, f.env, mf, f.clock, Limits{time.Second, 2 * time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, mf
}

// An applied move intent is terminal: the order was given.
func TestMovementAppliedIsTerminal(t *testing.T) {
	f, m := newMovementFixture(t)
	r, err := f.run()
	v := r.Progress.View()
	if err != nil || m.writes != 1 || v.Stage != domain.Completed || v.Unresolved {
		t.Fatal(r, err, m)
	}
	if m.last.Tick != 101 {
		t.Fatal("dispatch tick is not the draft prerequisite's", m.last.Tick)
	}
	if r, err = f.run(); err != nil || m.writes != 1 {
		t.Fatal(r, err)
	}
}

func TestMovementRefusedIsUnsuccessful(t *testing.T) {
	f, m := newMovementFixture(t)
	m.kind = domain.ReceiptRefused
	r, err := f.run()
	if err != nil || r.Progress.View().Stage != domain.Unsuccessful {
		t.Fatal(r, err)
	}
}

// The move waits for its draft to apply.
func TestMovementHeldWithoutDraft(t *testing.T) {
	f := newFixture(t)
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft-action", draft)
	move, _ := domain.NewMovement("pawn", domain.Cell{X: 3, Z: 4}, d.ID())
	a, _ := domain.NewMovementAction("move-action", move)
	plan, err := domain.NewPlan("movement-plan", 1, []domain.Action{d, a})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	f.plan, f.action = plan, a
	f.authority.Snapshot.Plan = plan.ID()
	m := &movementFake{}
	if f.executor, err = NewWithMovement(f.store, f.env, m, f.clock, Limits{time.Second, 2 * time.Second, time.Second}); err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	r, err := f.run()
	if err != ErrHeld || m.writes != 0 || r.Progress.View().Stage != domain.Pending {
		t.Fatal(r, err)
	}
}

// The hold-the-line move belongs to a routine method plan, so it dispatches
// under the root authority exactly like the draft it depends on (#70).
func TestMovementDispatchesRoundsPlanUnderRootAuthority(t *testing.T) {
	f, m := newMovementFixture(t)
	root := f.authority
	root.Snapshot.Plan = "player-plan"
	if err := f.executor.UpdateAuthority(root); err != nil {
		t.Fatal(err)
	}
	f.executor.roundsScope = roundsScopeFunc(func(_ context.Context, actual, target domain.GenerationSnapshot) error {
		if actual != root.Snapshot || target != f.authority.Snapshot {
			return ErrAuthority
		}
		return nil
	})
	r, err := f.run()
	if err != nil || m.writes != 1 || r.Progress.View().Stage != domain.Completed {
		t.Fatal(r, err, m)
	}
	if f.executor.current().Snapshot.Plan != "player-plan" {
		t.Fatal("routine execution changed player authority")
	}
}
