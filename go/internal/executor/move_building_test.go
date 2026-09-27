package executor

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type moveBuildingEnvironment struct {
	*environment
	inspected, applied, observed int
	gone, unsafe, foreign        bool
	effect                       domain.Effect
}

func (n *moveBuildingEnvironment) InspectMoveBuilding(_ context.Context, target Target) (MoveBuildingInspection, error) {
	n.inspected++
	if n.gone {
		return MoveBuildingInspection{}, ErrMoveBuildingAbsent
	}
	move, _ := target.Action.MoveBuilding()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe)})
	return MoveBuildingInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Move: move, Accepted: true, Emergency: emergency}, nil
}
func (n *moveBuildingEnvironment) ApplyMoveBuilding(_ context.Context, request MoveBuildingDispatch) (Receipt, error) {
	n.applied++
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *moveBuildingEnvironment) ObserveMoveBuilding(_ context.Context, p Placement, current domain.GenerationSnapshot) (MoveBuildingEvidence, error) {
	n.observed++
	move, _ := p.Action.MoveBuilding()
	if n.foreign {
		move, _ = domain.NewMoveBuilding("Thing_Other", move.Definition(), move.Cell(), move.Rotation())
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectPending
	}
	o := domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: domain.AfterDispatch}
	if effect == domain.EffectUnsuccessful {
		o.UnsuccessfulReason = domain.OutcomeNotAchieved
	}
	return MoveBuildingEvidence{Observation: o, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: true, Move: move, Installed: domain.Known(effect == domain.EffectCompleted)}, nil
}

func moveBuildingFixture(t *testing.T) (*fixture, *moveBuildingEnvironment) {
	t.Helper()
	f := newFixture(t)
	move, _ := domain.NewMoveBuilding("Thing_Bed7", "Bed", domain.Cell{X: 5, Z: 9}, domain.East)
	action, _ := domain.NewMoveBuildingAction("move-1", move)
	plan, _ := domain.NewPlan("tidy", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &moveBuildingEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableMoveBuilding(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestMoveBuildingDispatchesOnceAndCompletesOnInstall(t *testing.T) {
	f, n := moveBuildingFixture(t)
	if _, err := f.run(); err != nil || n.applied != 1 {
		t.Fatal(err, n.applied)
	}
	// Pending while the piece waits for a free hauler (or its user).
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.applied != 1 {
		t.Fatal(result, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.applied != 1 {
		t.Fatal(result, err)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.MoveBuildingAdmissions) != 1 {
		t.Fatal(state, err)
	}
}

func TestMoveBuildingRejectsForeignCompletion(t *testing.T) {
	f, n := moveBuildingFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect, n.foreign = domain.EffectCompleted, true
	if _, err := f.run(); !errors.Is(err, ErrEvidence) {
		t.Fatal(err)
	}
}

func TestMoveBuildingCancelledBlueprintIsUnsuccessful(t *testing.T) {
	f, n := moveBuildingFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	n.effect = domain.EffectUnsuccessful
	result, err := f.run()
	if err != nil || result.Progress.View().Stage != domain.Unsuccessful {
		t.Fatal(result, err)
	}
}

func TestMoveBuildingAbsentOrUnsafeNeverDispatches(t *testing.T) {
	f, n := moveBuildingFixture(t)
	n.unsafe = true
	if _, err := f.run(); err == nil || n.applied != 0 {
		t.Fatal("unsafe move dispatched", err)
	}
	n.unsafe, n.gone = false, true
	if _, err := f.run(); !errors.Is(err, ErrHeld) || n.applied != 0 {
		t.Fatal(err)
	}
	if stage := f.progress(t).Stage; stage != domain.Cancelled {
		t.Fatal("absent building left the action open", stage)
	}
}
