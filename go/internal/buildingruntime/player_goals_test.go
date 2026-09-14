package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerGoalRequest(id string, kind domain.GoalKind) store.GoalCreateSubmissionRequest {
	w := playerSubmission().World
	return store.GoalCreateSubmissionRequest{
		RequestID: id,
		Kind:      kind,
		Snapshot:  domain.GenerationSnapshot{Colony: w.Colony, Load: w.Load, Map: w.Map, Plan: "plan", Direction: 1},
		Tick:      10,
	}
}

// Activating a maintained goal records player direction and nothing more: it
// acquires no authority, issues no native command and commits no plan.
func TestPlayerGoalCreateReplayAndConflict(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	ctx := context.Background()
	q := playerGoalRequest("goal-create", domain.EnsureFoodSupplyGoal)
	result, created, err := p.SubmitGoalCreate(ctx, q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	if result.State.Goal.Source != domain.PlayerGoal || result.State.Goal.Status != domain.GoalActive || result.State.Goal.Need != domain.NeedDeficit {
		t.Fatal("goal not player-activated", result.State.Goal)
	}
	bindings, err := p.PlayerGoals(ctx, q.World())
	if err != nil || len(bindings) != 1 || bindings[domain.EnsureFoodSupplyGoal] != result.Goal {
		t.Fatal(bindings, err)
	}
	// A replay is answered from the journal without re-reading native identity.
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitGoalCreate(ctx, q)
	if err != nil || created || replay.Goal != result.Goal || replay.Request != q || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Kind = domain.MaintainWoodGoal
	if _, _, err = p.SubmitGoalCreate(ctx, changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.err = nil
	found, err := p.LookupGoalCreateSubmission(ctx, q.RequestID)
	if err != nil || found.Request != q {
		t.Fatal(found, err)
	}
	if _, err = db.LoadGoal(ctx, result.Goal); err != nil {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("goal activation changed native control")
	}
}

func TestPlayerGoalCreateFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	ctx := context.Background()
	q := playerGoalRequest("goal-fresh", domain.MaintainWasteGoal)
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitGoalCreate(ctx, q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupGoalCreateSubmission(ctx, q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World()
	result, created, err := p.SubmitGoalCreate(ctx, q)
	if err != nil || !created || result.State.Goal.Priority != 3 {
		t.Fatal(result, created, err)
	}
	if err = p.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if _, _, err = p.SubmitGoalCreate(ctx, q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}

// The player cancellation path is reachability over the existing store
// cancellation: same CAS token, same world bound, same terminal state.
func TestPlayerCancelGoalWorldBoundRevisionAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	ctx := context.Background()
	q := playerGoalRequest("goal-cancel", domain.EnsureBasicPowerGoal)
	result, _, err := p.SubmitGoalCreate(ctx, q)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = p.CancelGoal(ctx, q.World(), result.Goal, result.State.Revision+1); !errors.Is(err, store.ErrConflict) {
		t.Fatal("stale revision accepted", err)
	}
	if _, err = p.CancelGoal(ctx, q.World(), "absent", result.State.Revision); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world.Load = "replacement"
	if _, err = p.CancelGoal(ctx, q.World(), result.Goal, result.State.Revision); !errors.Is(err, store.ErrConflict) {
		t.Fatal("cancelled against a replaced world", err)
	}
	live, err := db.LoadGoal(ctx, result.Goal)
	if err != nil || live.Goal.Status != domain.GoalActive {
		t.Fatal("refused cancellation changed the goal", live, err)
	}
	worlds.world = q.World()
	cancelled, err := p.CancelGoal(ctx, q.World(), result.Goal, result.State.Revision)
	if err != nil || cancelled.Goal.Status != domain.GoalCancelled {
		t.Fatal(cancelled, err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("cancellation changed native control")
	}
	if err = p.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if _, err = p.CancelGoal(ctx, q.World(), result.Goal, cancelled.Revision); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
