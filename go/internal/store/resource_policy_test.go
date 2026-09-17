package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func resourcePolicyRequest(id, resource string, reserve int64) ResourcePolicySubmissionRequest {
	return ResourcePolicySubmissionRequest{
		RequestID: id,
		World:     World{Colony: "colony", Load: "load", Map: 0},
		Patch:     domain.ResourcePolicyPatch{Resource: resource, Reserve: domain.Some(reserve)},
	}
}
func resourceSpendingRequest(id, resource string, spending domain.ResourceSpending) ResourcePolicySubmissionRequest {
	return ResourcePolicySubmissionRequest{
		RequestID: id,
		World:     World{Colony: "colony", Load: "load", Map: 0},
		Patch:     domain.ResourcePolicyPatch{Resource: resource, Spending: domain.Some(spending)},
	}
}

func TestResourcePolicySubmissionReplayConflictAndReopen(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	request := resourcePolicyRequest("request", "Steel", 250)
	first, created, err := s.SubmitResourcePolicy(ctx, request)
	if err != nil || !created || first.Plan == "" || first.Action == "" || first.Revision != 1 {
		t.Fatal(first, created, err)
	}
	if first.Applied.Reserve() != 250 || first.Applied.Spending() != domain.ResourceSpendingNormal || first.Current != first.Applied {
		t.Fatal("incorrect applied directive", first.Applied)
	}
	replay, created, err := s.SubmitResourcePolicy(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal(replay, created, err)
	}
	for _, change := range []func(*ResourcePolicySubmissionRequest){
		func(v *ResourcePolicySubmissionRequest) { v.World.Map = 1 },
		func(v *ResourcePolicySubmissionRequest) { v.World.Load = "other" },
		func(v *ResourcePolicySubmissionRequest) { v.World.Colony = "other" },
		func(v *ResourcePolicySubmissionRequest) { v.Patch.Reserve = domain.Some(int64(10)) },
		func(v *ResourcePolicySubmissionRequest) { v.Patch.Resource = "Plasteel" },
	} {
		changed := request
		change(&changed)
		if _, _, err := s.SubmitResourcePolicy(ctx, changed); !errors.Is(err, ErrConflict) {
			t.Fatal("semantic conflict accepted", err)
		}
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err = s.SubmitResourcePolicy(ctx, request)
	if err != nil || created || replay != first {
		t.Fatal("reopen replay changed", err)
	}
	found, err := s.LookupResourcePolicySubmission(ctx, "request")
	if err != nil || found != first {
		t.Fatal(found, err)
	}
	state, err := s.LoadPlan(ctx, first.Plan)
	if err != nil || len(state.Spec.Actions()) != 1 || state.Progress[0].View().Stage != domain.Pending {
		t.Fatal(state, err)
	}
	value, ok := state.Spec.Actions()[0].ProductionPolicy()
	if !ok || len(value.Floors()) != 1 || value.Floors()[0].Resource != "Steel" || value.Floors()[0].Floor != 250 || len(value.Stopped()) != 0 {
		t.Fatal("the committed action must carry the merged whole", value)
	}
}

// Each command patches one half of one resource and dispatches the world's
// whole merged policy, rebuilt from every recorded directive.
func TestResourcePolicyMergesHalvesAndDispatchesWholeSet(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	if _, _, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("r1", "Steel", 250)); err != nil {
		t.Fatal(err)
	}
	// Changing spending preserves the reserve already established.
	second, _, err := s.SubmitResourcePolicy(ctx, resourceSpendingRequest("r2", "Steel", domain.ResourceSpendingStop))
	if err != nil {
		t.Fatal(err)
	}
	if second.Applied.Reserve() != 250 || second.Applied.Spending() != domain.ResourceSpendingStop {
		t.Fatal("spending change clobbered the reserve", second.Applied)
	}
	// A second resource is added without disturbing the first.
	third, _, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("r3", "WoodLog", 100))
	if err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, third.Plan)
	if err != nil {
		t.Fatal(err)
	}
	value, ok := state.Spec.Actions()[0].ProductionPolicy()
	if !ok || len(value.Floors()) != 2 || value.Floors()[0].Resource != "Steel" || value.Floors()[1].Resource != "WoodLog" {
		t.Fatal("the whole merged set must be dispatched, not a diff", value)
	}
	if len(value.Stopped()) != 1 || value.Stopped()[0] != "Steel" {
		t.Fatal("an earlier resource's restriction must survive a later resource's change", value.Stopped())
	}
	// A zero reserve removes the floor and leaves the restriction standing.
	fourth, _, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("r4", "Steel", 0))
	if err != nil {
		t.Fatal(err)
	}
	if fourth.Applied.Spending() != domain.ResourceSpendingStop {
		t.Fatal("zeroing the reserve clobbered the restriction", fourth.Applied)
	}
	state, err = s.LoadPlan(ctx, fourth.Plan)
	if err != nil {
		t.Fatal(err)
	}
	value, _ = state.Spec.Actions()[0].ProductionPolicy()
	if len(value.Floors()) != 1 || value.Floors()[0].Resource != "WoodLog" || len(value.Stopped()) != 1 || value.Stopped()[0] != "Steel" {
		t.Fatal("a zero reserve must drop the floor and keep the stop", value)
	}
	directives, err := s.ResourcePolicies(ctx, World{Colony: "colony", Load: "load", Map: 0})
	if err != nil || len(directives) != 2 || directives[0].Resource() != "Steel" || directives[1].Resource() != "WoodLog" {
		t.Fatal(directives, err)
	}
	// Replaying an older request reports what it did, alongside the newer
	// current value.
	old, created, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("r1", "Steel", 250))
	if err != nil || created || old.Applied.Reserve() != 250 || old.Current.Reserve() != 0 || old.Current.Spending() != domain.ResourceSpendingStop {
		t.Fatal("replay must not re-merge", old, created, err)
	}
	if _, err = s.CurrentResourcePolicy(ctx, World{Colony: "colony", Load: "load", Map: 0}, "Plasteel"); !errors.Is(err, ErrNotFound) {
		t.Fatal("an unnamed resource has no directive", err)
	}
}

// The player submission path and the autopilot's own production-policy writer
// must both be reachable: the player commits its own one-action plan through
// createPlan directly, bypassing the autopilot-goal-bound admission that
// CommitGoalMethod applies, while the routine planner keeps committing through
// that gate untouched. Both produce the same domain.ProductionPolicyAction for
// the same unchanged executor/bridge dispatch.
func TestResourcePolicyCoexistsWithAutopilotProductionPolicy(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	player, _, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("player", "Steel", 250))
	if err != nil {
		t.Fatal(err)
	}
	// A player submission is bound to no goal at all.
	var bound int
	if err = s.db.QueryRow("SELECT count(*) FROM goal_methods WHERE plan_id=?", string(player.Plan)).Scan(&bound); err != nil || bound != 0 {
		t.Fatal("a player submission must not be bound to a goal", bound, err)
	}
	r := routineRequest()
	r.Policy.ResourceReserves = map[policy.Resource]int64{"Plasteel": 50}
	r.Policy.MaxDevelopmentProjects = 8
	r.Facts.Workers = domain.Known(8)
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.ProductionPolicy)
	if g.Goal.Source != domain.AutopilotGoal || g.Goal.Need != domain.NeedDeficit {
		t.Fatal("expected an autopilot production policy deficit", g.Goal)
	}
	// The player-submitted action reaches the same unchanged dispatch
	// admission-evidence path the autopilot's own production policy uses:
	// PrepareProductionPolicy is shared verbatim, with no player-specific
	// branch, and the Commitments/Drills rows another system owns are still
	// carried through it to be resent verbatim.
	state, err := s.LoadPlan(ctx, player.Plan)
	if err != nil {
		t.Fatal(err)
	}
	value, ok := state.Spec.Actions()[0].ProductionPolicy()
	if !ok {
		t.Fatal("the player path must produce a production policy action")
	}
	snapshot := scope()
	snapshot.Plan, snapshot.Revision, snapshot.Native = player.Plan, 1, 3
	progress, err := s.PrepareProductionPolicy(ctx, player.Plan, player.Action, ProductionPolicyAdmission{
		Snapshot:      snapshot,
		Tick:          11,
		Floors:        value.Floors(),
		Stopped:       value.Stopped(),
		Commitments:   []ProductionCommitment{{Resource: "Plasteel", Count: 12}},
		SnapshotToken: "token",
	})
	if err != nil || progress.View().Stage != domain.Prepared {
		t.Fatal("the shared production policy admission must accept a player-submitted action", progress, err)
	}
	// The player path keeps working alongside the autopilot goal, and the
	// resource the autopilot configured is untouched by it: the two writers
	// share the native state, not the controller's player-declared policy.
	later, _, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("player-2", "Steel", 300))
	if err != nil {
		t.Fatal(err)
	}
	state, err = s.LoadPlan(ctx, later.Plan)
	if err != nil {
		t.Fatal(err)
	}
	merged, _ := state.Spec.Actions()[0].ProductionPolicy()
	if len(merged.Floors()) != 1 || merged.Floors()[0].Floor != 300 {
		t.Fatal("the player path must keep dispatching its own merged set", merged)
	}
}

func TestResourcePolicySubmissionAtomicFailure(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	ctx := context.Background()
	if _, err := s.db.Exec("CREATE TRIGGER fail_resource_policy BEFORE INSERT ON resource_policy_submissions BEGIN SELECT RAISE(ABORT,'fixture failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("request", "Steel", 250)); err == nil {
		t.Fatal("trigger did not fail")
	}
	for _, table := range []string{"plans", "actions", "submissions", "resource_policy_submissions", "resource_policies"} {
		var count int
		if err := s.db.QueryRow("SELECT count(*) FROM " + table).Scan(&count); err != nil || count != 0 {
			t.Fatal("partial transaction", table, count, err)
		}
	}
	if _, err := s.db.Exec("DROP TRIGGER fail_resource_policy"); err != nil {
		t.Fatal(err)
	}
	if _, created, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("request", "Steel", 250)); err != nil || !created {
		t.Fatal(err)
	}
}

func TestResourcePolicySubmissionValidationRejected(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	ctx := context.Background()
	for _, change := range []func(*ResourcePolicySubmissionRequest){
		func(v *ResourcePolicySubmissionRequest) { v.RequestID = "bad\x00id" },
		func(v *ResourcePolicySubmissionRequest) { v.World.Map = -1 },
		func(v *ResourcePolicySubmissionRequest) { v.World.Load = "" },
		func(v *ResourcePolicySubmissionRequest) { v.Patch = domain.ResourcePolicyPatch{} },
		func(v *ResourcePolicySubmissionRequest) {
			v.Patch.Spending = domain.Some(domain.ResourceSpendingStop)
		},
		func(v *ResourcePolicySubmissionRequest) { v.Patch.Reserve = domain.Some(int64(-1)) },
	} {
		request := resourcePolicyRequest("request", "Steel", 250)
		change(&request)
		if _, _, err := s.SubmitResourcePolicy(ctx, request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	if _, err := s.LookupResourcePolicySubmission(ctx, ""); err == nil {
		t.Fatal("invalid lookup id accepted")
	}
}

// Resource policy submissions share the one submission-identity namespace with
// every other plan-bearing submission; a request id used by one kind must not
// silently resolve as the other.
func TestResourcePolicySubmissionSharesNamespaceWithBuilding(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	if _, _, err := s.SubmitBuilding(ctx, submissionRequest(t, "shared")); err != nil {
		t.Fatal(err)
	}
	if _, _, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("shared", "Steel", 250)); !errors.Is(err, ErrConflict) {
		t.Fatal("cross-kind request id collision accepted", err)
	}
	if _, _, err := s.SubmitResourcePolicy(ctx, resourcePolicyRequest("policy-only", "Steel", 250)); err != nil {
		t.Fatal(err)
	}
	if _, err := s.LookupSubmission(ctx, "policy-only"); err == nil {
		t.Fatal("building lookup accepted a resource policy submission")
	}
}
