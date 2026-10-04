package store

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A player goal is derived from the rebuilt goals (#1006): after a world
// change rebuilds a fresh store from the save's goal blob, it still lists in
// PlayerGoals under a new load token; a rebuild without it drops it.
func TestPlayerGoalSurvivesWorldChange(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	first, _, err := s.SubmitGoalCreate(ctx, goalCreateRequest("create", domain.MaintainResourceGoal))
	if err != nil {
		t.Fatal(err)
	}
	blob, err := json.Marshal(GovernorGoalBlob{SchemaVersion: GovernorStateSchemaVersion, Goal: first.State.(GoalState).Goal, Revision: first.State.OwnerRevision()})
	if err != nil {
		t.Fatal(err)
	}
	saved := map[string]string{GovernorGoalKeyPrefix + string(first.Goal): string(blob)}
	reloaded := open(t, memoryPath(t))
	if err = reloaded.RebuildGoals(ctx, saved, nil); err != nil {
		t.Fatal(err)
	}
	w := World{Colony: scope().Colony, Load: "reloaded", Map: scope().Map}
	bindings, err := reloaded.PlayerGoals(ctx, w)
	if err != nil || len(bindings) != 1 || bindings[domain.MaintainResourceGoal].OwnerID() != first.Goal {
		t.Fatal(bindings, err)
	}
	if other, err := reloaded.PlayerGoals(ctx, World{Colony: "elsewhere", Load: "reloaded"}); err != nil || len(other) != 0 {
		t.Fatal(other, err)
	}
	if err = s.RebuildGoals(ctx, map[string]string{}, nil); err != nil {
		t.Fatal(err)
	}
	if gone, err := s.PlayerGoals(ctx, first.Request.World()); err != nil || len(gone) != 0 {
		t.Fatal("dropped goal still listed", gone, err)
	}
}

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
	g := first.State.(GoalState).Goal
	if g.Source != domain.PlayerGoal || g.Status != domain.GoalActive || g.Need != domain.NeedDeficit || g.Priority != 2 || g.Snapshot != scope() || g.Tick != 10 {
		t.Fatal("goal not activated as player direction", g)
	}
	if first.Goal != string(g.ID) || first.State.OwnerRevision() == 0 {
		t.Fatal(first)
	}
	// A player goal admits a method on exactly the unchanged terms an
	// autopilot goal does; nothing new gates or ungates it here.
	if _, err = s.CommitGoalMethod(ctx, g.ID, first.State.OwnerRevision(), "method", plan(t, "player-goal-plan", "player-goal-action")); err != nil {
		t.Fatal("player goal refused a method", err)
	}
	replay, created, err := s.SubmitGoalCreate(ctx, q)
	if err != nil || created || replay.Request != q || replay.Goal != first.Goal {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*GoalCreateSubmissionRequest){
		func(v *GoalCreateSubmissionRequest) { v.Kind = domain.MaintainResourceGoal },
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
	// Request replay is session-only (#1011): a reopened store forgets it.
	if _, err := s.LookupGoalCreateSubmission(ctx, q.RequestID); !errors.Is(err, ErrNotFound) {
		t.Fatal("replay survived a restart", err)
	}
	bindings, err := s.PlayerGoals(ctx, q.World())
	if err != nil || len(bindings) != 1 || bindings[domain.EnsureFoodSupplyGoal].OwnerID() != first.Goal {
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
	first, _, err := s.SubmitGoalCreate(ctx, goalCreateRequest("create", domain.MaintainResourceGoal))
	if err != nil {
		t.Fatal(err)
	}
	recovered, err := s.ReviewGoal(ctx, domain.GoalID(first.Goal), first.State.OwnerRevision(), scope(), 11, domain.NeedRecovered)
	if err != nil || recovered.Goal.Status != domain.GoalSatisfied || !recovered.Goal.RecoveryObserved {
		t.Fatal(recovered, err)
	}
	again, created, err := s.SubmitGoalCreate(ctx, GoalCreateSubmissionRequest{RequestID: "again", Kind: domain.MaintainResourceGoal, Snapshot: scope(), Tick: 12})
	if err != nil || !created || again.Goal != first.Goal {
		t.Fatal("live goal not reused", again, created, err)
	}
	if again.State.(GoalState).Goal.Status != domain.GoalActive || again.State.(GoalState).Goal.Need != domain.NeedDeficit ||
		again.State.(GoalState).Goal.Epoch != recovered.Goal.Epoch+1 || again.State.(GoalState).Goal.RecoveryObserved {
		t.Fatal("reactivation did not renew the goal", again.State.(GoalState).Goal)
	}
	cancelled, err := s.CancelPlayerGoal(ctx, again.Request.World(), again.Goal, again.State.OwnerRevision())
	if err != nil || cancelled.(GoalState).Goal.Status != domain.GoalCancelled {
		t.Fatal(cancelled, err)
	}
	fresh, created, err := s.SubmitGoalCreate(ctx, GoalCreateSubmissionRequest{RequestID: "fresh", Kind: domain.MaintainResourceGoal, Snapshot: scope(), Tick: 13})
	if err != nil || !created || fresh.Goal == first.Goal {
		t.Fatal("cancelled goal resurrected or not replaced", fresh, created, err)
	}
	if fresh.State.(GoalState).Goal.Status != domain.GoalActive || fresh.State.(GoalState).Goal.Need != domain.NeedDeficit || fresh.State.(GoalState).Goal.Epoch != 0 {
		t.Fatal(fresh.State.(GoalState).Goal)
	}
	// The superseded goal keeps its own cancelled history untouched.
	old, err := s.LoadGoal(ctx, domain.GoalID(first.Goal))
	if err != nil || old.Goal.Status != domain.GoalCancelled {
		t.Fatal(old, err)
	}
	bindings, err := s.PlayerGoals(ctx, fresh.Request.World())
	if err != nil || bindings[domain.MaintainResourceGoal].OwnerID() != fresh.Goal {
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
	if _, err = s.CancelPlayerGoal(ctx, World{Colony: "elsewhere", Load: "load"}, first.Goal, first.State.OwnerRevision()); !errors.Is(err, ErrNotFound) {
		t.Fatal("cancelled across worlds", err)
	}
	if _, err = s.CancelPlayerGoal(ctx, w, first.Goal, first.State.OwnerRevision()+1); !errors.Is(err, ErrConflict) {
		t.Fatal("stale revision accepted", err)
	}
	if _, err = s.CancelPlayerGoal(ctx, w, "absent", first.State.OwnerRevision()); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	if _, err = s.CancelPlayerGoal(ctx, World{}, first.Goal, first.State.OwnerRevision()); err == nil {
		t.Fatal("invalid world accepted")
	}
	state, err := s.LoadGoal(ctx, domain.GoalID(first.Goal))
	if err != nil || state.Goal.Status != domain.GoalActive {
		t.Fatal("refused cancellation still changed the goal", state, err)
	}
	cancelled, err := s.CancelPlayerGoal(ctx, w, first.Goal, state.Revision)
	if err != nil || cancelled.(GoalState).Goal.Status != domain.GoalCancelled {
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
	state, err := s.ReviewGoal(ctx, g.ID, 0, scope(), 10, domain.NeedDeficit)
	if err != nil {
		t.Fatal(err)
	}
	cancelled, err := s.CancelPlayerGoal(ctx, World{Colony: scope().Colony, Load: scope().Load, Map: scope().Map}, string(g.ID), state.Revision)
	if err != nil || cancelled.(GoalState).Goal.Status != domain.GoalCancelled || cancelled.(GoalState).Goal.Source != domain.AutopilotGoal {
		t.Fatal(cancelled, err)
	}
}
