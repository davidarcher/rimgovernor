package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func goalCreateRequest(id string, kind domain.GoalKind) GoalCreateSubmissionRequest {
	return GoalCreateSubmissionRequest{RequestID: id, Kind: kind, Snapshot: scope(), Tick: 10}
}

func TestGoalCreateActivatesPlayerSourcedGoalAndReplays(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	q := goalCreateRequest("create-food", domain.EnsureFoodSupplyGoal)
	first, created, err := s.SubmitGoalCreate(ctx, q)
	if err != nil || !created {
		t.Fatal(first, created, err)
	}
	// Player direction is the deficit assertion: the goal is active and in
	// deficit at once, without waiting for an autopilot review to observe it.
	g := first.State.Goal
	if g.Source != domain.PlayerGoal || g.Status != domain.GoalActive || g.Need != domain.NeedDeficit || g.Priority != 2 || g.Snapshot != scope() || g.Tick != 10 {
		t.Fatal("goal not activated as player direction", g)
	}
	if first.Goal != g.ID || first.State.Revision == 0 {
		t.Fatal(first)
	}
	// A player goal admits a method on exactly the unchanged terms an
	// autopilot goal does; nothing new gates or ungates it here.
	if _, err = s.CommitGoalMethod(ctx, g.ID, first.State.Revision, "method", plan(t, "player-goal-plan", "player-goal-action")); err != nil {
		t.Fatal("player goal refused a method", err)
	}
	replay, created, err := s.SubmitGoalCreate(ctx, q)
	if err != nil || created || replay.Request != q || replay.Goal != first.Goal {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*GoalCreateSubmissionRequest){
		func(v *GoalCreateSubmissionRequest) { v.Kind = domain.MaintainWoodGoal },
		func(v *GoalCreateSubmissionRequest) { v.Tick = 11 },
		func(v *GoalCreateSubmissionRequest) { v.Snapshot.Load = "other" },
	} {
		changed := q
		change(&changed)
		if _, _, err := s.SubmitGoalCreate(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	found, err := s.LookupGoalCreateSubmission(ctx, q.RequestID)
	if err != nil || found.Request != q || found.Goal != first.Goal {
		t.Fatal(found, err)
	}
	bindings, err := s.PlayerGoals(ctx, q.World())
	if err != nil || len(bindings) != 1 || bindings[domain.EnsureFoodSupplyGoal] != first.Goal {
		t.Fatal(bindings, err)
	}
}

func TestGoalCreateRejectsUnsupportedKindAndInvalidScope(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	for _, bad := range []GoalCreateSubmissionRequest{
		{RequestID: "bad-kind", Kind: domain.GoalKind("EnsureComfort"), Snapshot: scope(), Tick: 1},
		{RequestID: "bad-kind-empty", Snapshot: scope(), Tick: 1},
		{RequestID: "bad-tick", Kind: domain.MaintainWasteGoal, Snapshot: scope(), Tick: -1},
		{RequestID: "bad-scope", Kind: domain.MaintainWasteGoal, Tick: 1},
		{RequestID: "", Kind: domain.MaintainWasteGoal, Snapshot: scope(), Tick: 1},
	} {
		if _, created, err := s.SubmitGoalCreate(ctx, bad); err == nil || created {
			t.Fatal("invalid goal activation accepted", bad.RequestID)
		}
	}
	if goals, err := s.PlayerGoals(ctx, World{Colony: "colony", Load: "load"}); err != nil || len(goals) != 0 {
		t.Fatal(goals, err)
	}
}

// Re-activating a live player goal reuses its identity and lets ReviewGoal's
// own epoch rule do the reopening work; a cancelled
// one is never resurrected, because cancellation is terminal in ReviewGoal.
func TestGoalCreateReopensLiveGoalAndReplacesCancelledOne(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	first, _, err := s.SubmitGoalCreate(ctx, goalCreateRequest("create", domain.MaintainWoodGoal))
	if err != nil {
		t.Fatal(err)
	}
	recovered, err := s.ReviewGoal(ctx, first.Goal, first.State.Revision, scope(), 11, domain.NeedRecovered, false)
	if err != nil || recovered.Goal.Status != domain.GoalSatisfied || !recovered.Goal.RecoveryObserved {
		t.Fatal(recovered, err)
	}
	again, created, err := s.SubmitGoalCreate(ctx, GoalCreateSubmissionRequest{RequestID: "again", Kind: domain.MaintainWoodGoal, Snapshot: scope(), Tick: 12})
	if err != nil || !created || again.Goal != first.Goal {
		t.Fatal("live goal not reused", again, created, err)
	}
	if again.State.Goal.Status != domain.GoalActive || again.State.Goal.Need != domain.NeedDeficit ||
		again.State.Goal.Epoch != recovered.Goal.Epoch+1 || again.State.Goal.RecoveryObserved {
		t.Fatal("reactivation did not renew the goal", again.State.Goal)
	}
	cancelled, err := s.CancelPlayerGoal(ctx, again.Request.World(), again.Goal, again.State.Revision)
	if err != nil || cancelled.Goal.Status != domain.GoalCancelled {
		t.Fatal(cancelled, err)
	}
	fresh, created, err := s.SubmitGoalCreate(ctx, GoalCreateSubmissionRequest{RequestID: "fresh", Kind: domain.MaintainWoodGoal, Snapshot: scope(), Tick: 13})
	if err != nil || !created || fresh.Goal == first.Goal {
		t.Fatal("cancelled goal resurrected or not replaced", fresh, created, err)
	}
	if fresh.State.Goal.Status != domain.GoalActive || fresh.State.Goal.Need != domain.NeedDeficit || fresh.State.Goal.Epoch != 0 {
		t.Fatal(fresh.State.Goal)
	}
	// The superseded goal keeps its own cancelled history untouched.
	old, err := s.LoadGoal(ctx, first.Goal)
	if err != nil || old.Goal.Status != domain.GoalCancelled {
		t.Fatal(old, err)
	}
	bindings, err := s.PlayerGoals(ctx, fresh.Request.World())
	if err != nil || bindings[domain.MaintainWoodGoal] != fresh.Goal {
		t.Fatal(bindings, err)
	}
	// The earlier request ID still reports the identity it actually activated.
	replay, err := s.LookupGoalCreateSubmission(ctx, "create")
	if err != nil || replay.Goal != first.Goal {
		t.Fatal(replay, err)
	}
}

// Cancellation reuses the unchanged store cancellation body; the player path
// adds a world bound and the same local CAS token, not new semantics.
func TestCancelPlayerGoalBoundsWorldAndRevision(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	first, _, err := s.SubmitGoalCreate(ctx, goalCreateRequest("create", domain.EnsureBasicDefenseGoal))
	if err != nil {
		t.Fatal(err)
	}
	w := first.Request.World()
	if _, err = s.CancelPlayerGoal(ctx, World{Colony: "elsewhere", Load: "load"}, first.Goal, first.State.Revision); !errors.Is(err, ErrNotFound) {
		t.Fatal("cancelled across worlds", err)
	}
	if _, err = s.CancelPlayerGoal(ctx, w, first.Goal, first.State.Revision+1); !errors.Is(err, ErrConflict) {
		t.Fatal("stale revision accepted", err)
	}
	if _, err = s.CancelPlayerGoal(ctx, w, "absent", first.State.Revision); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	if _, err = s.CancelPlayerGoal(ctx, World{}, first.Goal, first.State.Revision); err == nil {
		t.Fatal("invalid world accepted")
	}
	state, err := s.LoadGoal(ctx, first.Goal)
	if err != nil || state.Goal.Status != domain.GoalActive {
		t.Fatal("refused cancellation still changed the goal", state, err)
	}
	cancelled, err := s.CancelPlayerGoal(ctx, w, first.Goal, state.Revision)
	if err != nil || cancelled.Goal.Status != domain.GoalCancelled {
		t.Fatal(cancelled, err)
	}
}

// An autopilot goal is cancellable through the same player path: any
// recorded goal ID resolves.
func TestCancelPlayerGoalCancelsAutopilotGoalInSameWorld(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	g, err := domain.NewGoal("routine-goal", domain.AutopilotGoal, 2, scope(), 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreateGoal(ctx, g); err != nil {
		t.Fatal(err)
	}
	state, err := s.ReviewGoal(ctx, g.ID, 0, scope(), 10, domain.NeedDeficit, false)
	if err != nil {
		t.Fatal(err)
	}
	cancelled, err := s.CancelPlayerGoal(ctx, World{Colony: scope().Colony, Load: scope().Load, Map: scope().Map}, g.ID, state.Revision)
	if err != nil || cancelled.Goal.Status != domain.GoalCancelled || cancelled.Goal.Source != domain.AutopilotGoal {
		t.Fatal(cancelled, err)
	}
}
