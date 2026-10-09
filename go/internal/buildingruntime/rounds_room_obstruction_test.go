package buildingruntime

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A room whose ring holds a claimable ruin and whose interior holds an
// impassable tree and a haulable item commits the claim first, then the cut,
// then the haul: each pass commits the first kind still owed, as one
// method named for the room and carrying the existing action kinds.
func TestCommitObstructionsCommitsTheFirstKindOwed(t *testing.T) {
	target := func(id, def string, x, z int32) policy.ClearanceTarget {
		c := domain.Cell{X: x, Z: z}
		return policy.ClearanceTarget{EntityID: id, DefName: def, Minimum: c, Maximum: c, Class: "foreign"}
	}
	ops := []policy.Operation{
		{Kind: policy.OpHaulOut, Targets: []policy.ClearanceTarget{target("Thing_3", "Steel", 2, 2)}},
		{Kind: policy.OpCut, Targets: []policy.ClearanceTarget{target("Thing_2", "Plant_TreeOak", 1, 2)}},
		{Kind: policy.OpClaim, Targets: []policy.ClearanceTarget{target("Thing_1", "Wall", 0, 1)}},
	}
	cases := []struct {
		name   string
		ops    []policy.Operation
		kind   domain.ActionKind
		design string
		thing  string
	}{
		{"claim first", ops, domain.ClaimBuildingAction, "", ""},
		{"then cut", ops[:2], domain.CoverClearanceAction, domain.CoverClearanceCutPlant, "Thing_2"},
		{"then haul", ops[:1], domain.CoverClearanceAction, domain.CoverClearanceHaul, "Thing_3"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			p, db, _, _ := refrigerationFixture(t, false)
			ctx := context.Background()
			state := p.reviewer.player.session.State()
			review, err := db.LoadRounds(ctx)
			if err != nil {
				t.Fatal(err)
			}
			var goal domain.ConcernID
			for _, binding := range review.Standards {
				if binding.Concern == policy.MaintainRefrigeration {
					goal = binding.Standard
				}
			}
			owner, err := db.LoadStandard(ctx, goal)
			if err != nil {
				t.Fatal(err)
			}
			player := p.reviewer.player
			player.mu.Lock()
			epoch := player.epoch
			player.mu.Unlock()
			works := []roomWork{{rr: roomReconcile{room: policy.PlannedRoom{Interior: policy.Rectangle{X: 1, Z: 1, Width: 2, Height: 2}}, name: "bunk", reason: "bunk"}, ops: tc.ops}}
			result, done, err := p.commitObstructions(ctx, epoch, state, owner, works)
			if err != nil || !done || result.Verdict != BuildingReasonAdmitted {
				t.Fatalf("wave = %+v done=%v, %v", result.Verdict, done, err)
			}
			after, err := db.LoadStandard(ctx, goal)
			if err != nil {
				t.Fatal(err)
			}
			last := after.Methods[len(after.Methods)-1]
			if !strings.HasPrefix(string(last.Method), "bunk-") {
				t.Fatalf("method %q is not the room's", last.Method)
			}
			plan, err := db.LoadPlan(ctx, last.Plan)
			if err != nil {
				t.Fatal(err)
			}
			actions := plan.Spec.Actions()
			if len(actions) != 1 || actions[0].Kind() != tc.kind {
				t.Fatalf("actions = %v, want one %s", actions, tc.kind)
			}
			if c, ok := actions[0].CoverClearance(); ok && (c.Designation() != tc.design || c.Thing() != tc.thing) {
				t.Fatalf("clearance = %s %s, want %s %s", c.Designation(), c.Thing(), tc.design, tc.thing)
			}
		})
	}
}

// A held foreign thing names itself in the wait key.
func TestWaitingHeldNamesTheBlocker(t *testing.T) {
	work := roomWork{holds: []policy.ReconcileHold{{Cell: domain.Cell{X: 4, Z: 5}, Def: "AncientShrine", Reason: "casket"}}}
	got := waitingHeld(context.Background(), "kitchen", work).Verdict.Refusal.Subject
	if got != "kitchen_reconcile:held:AncientShrine@4,5:casket" {
		t.Fatalf("subject = %q", got)
	}
}
