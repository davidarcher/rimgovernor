package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func playerResourcePolicyRequest(id, resource string, reserve int64) store.ResourcePolicySubmissionRequest {
	return store.ResourcePolicySubmissionRequest{
		RequestID: id,
		World:     playerSubmission().World,
		Patch:     domain.ResourcePolicyPatch{Resource: resource, Reserve: domain.Some(reserve)},
	}
}

// A resource policy change is player-command-driven, unlike the
// autopilot-goal-bound RoutineProductionPolicyPlanner: submission alone commits
// the one-action plan that pushes the whole merged policy, and never acquires
// authority or issues a native command.
func TestPlayerResourcePolicySubmissionReplayAndSharedFamilyNamespace(t *testing.T) {
	t.Parallel()
	p, db, s, worlds := playerFixture(t)
	ctx := context.Background()
	q := playerResourcePolicyRequest("resource-policy-submit", "Steel", 250)
	result, created, err := p.SubmitResourcePolicy(ctx, q)
	if err != nil || !created {
		t.Fatal(result, created, err)
	}
	state, err := db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	value, ok := state.Spec.Actions()[0].ProductionPolicy()
	if !ok || len(value.Floors()) != 1 || value.Floors()[0].Resource != "Steel" || value.Floors()[0].Floor != 250 ||
		state.Progress[0].View().Stage != domain.Pending || state.Progress[0].View().Attempt != 0 {
		t.Fatal(state)
	}
	all, err := p.ResourcePolicies(ctx, q.World)
	if err != nil || len(all) != 1 || all[0] != result.Applied {
		t.Fatal(all, err)
	}
	worlds.err = errors.New("world unavailable")
	replay, created, err := p.SubmitResourcePolicy(ctx, q)
	if err != nil || created || replay != result || worlds.calls != 1 {
		t.Fatal(replay, created, err, worlds.calls)
	}
	changed := q
	changed.Patch.Reserve = domain.Some(int64(300))
	if _, _, err = p.SubmitResourcePolicy(ctx, changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	building := playerSubmission()
	building.RequestID = q.RequestID
	if _, _, err = p.Submit(ctx, building); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	worlds.err = nil
	building.RequestID = "building-submit"
	if _, _, err = p.Submit(ctx, building); err != nil {
		t.Fatal(err)
	}
	q.RequestID = building.RequestID
	if _, _, err = p.SubmitResourcePolicy(ctx, q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if s.acquires.Load() != 0 || s.manuals.Load() != 0 || p.State().Enabled {
		t.Fatal("submission changed native control")
	}
}

func TestPlayerResourcePolicyFreshWorldAndClosedPlayer(t *testing.T) {
	t.Parallel()
	p, db, _, worlds := playerFixture(t)
	ctx := context.Background()
	q := playerResourcePolicyRequest("resource-policy-fresh", "Steel", 250)
	worlds.world.Load = "replacement"
	if _, _, err := p.SubmitResourcePolicy(ctx, q); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
	if _, err := db.LookupResourcePolicySubmission(ctx, q.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
	worlds.world = q.World
	if _, created, err := p.SubmitResourcePolicy(ctx, q); err != nil || !created {
		t.Fatal(created, err)
	}
	found, err := p.LookupResourcePolicySubmission(ctx, q.RequestID)
	if err != nil || found.Request != q {
		t.Fatal(found, err)
	}
	if err := p.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if _, _, err := p.SubmitResourcePolicy(ctx, q); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}
