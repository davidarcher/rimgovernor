package buildingruntime

import (
	"context"
	"fmt"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// methodPlan is the plan decision bound to method (or a stub of it).
func methodPlan(t *testing.T, decision store.BuildingMethodDecision, method domain.MethodID) domain.PlanID {
	t.Helper()
	for _, m := range decision.Goal.Methods {
		if matchesMethod(m.Method, method) {
			return m.Plan
		}
	}
	t.Fatal("method not admitted", method, decision.Goal.Methods)
	return ""
}

// matchesMethod accepts want exactly or as a stub for any target.
func matchesMethod(method, want domain.MethodID) bool {
	return method == want || strings.HasPrefix(string(method), string(want)+"@")
}

func TestExcavationMethodTargetRoundTrip(t *testing.T) {
	target, err := policy.ParseExcavationKey("20.15.-1.0.3")
	if err != nil {
		t.Fatal(err)
	}
	stage, parsed, ok := ExcavationMethod(excavationStageMethod(2, target))
	if !ok || stage != 2 || parsed.Key() != target.Key() || parsed.Door != (domain.Cell{X: 17, Z: 15}) {
		t.Fatal(stage, parsed, ok)
	}
	for _, bad := range []domain.MethodID{"excavation-door@20.15.-1.0.3", "excavation-stage-x@20.15.-1.0.3", "excavation-stage-1@20.15.-1.0.3.10.12.7.7", "shell"} {
		if IsExcavationMethod(bad) {
			t.Fatal("accepted", bad)
		}
	}
}

// A tunnel stage held past excavationStallTicks is cancelled; within the
// grace it stands.
func TestCancelStalledExcavation(t *testing.T) {
	t.Parallel()
	planner, db, _ := buriedOreFixture(t)
	ctx := context.Background()
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	planID, _ := tunnelStage(t, db)
	if planID == "" {
		t.Fatal("no tunnel stage")
	}
	action := domain.ActionID(fmt.Sprintf("%s-%d", planID, 0))
	if _, err := db.Hold(ctx, planID, action, []domain.HeldReason{domain.HeldNotReady}, 7); err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal store.StandardState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainResource {
			if goal, err = db.LoadStandard(ctx, binding.Goal); err != nil {
				t.Fatal(err)
			}
		}
	}
	var plan store.PlanState
	if err := cancelStalledExcavation(ctx, db, goal, 7+excavationStallTicks-1); err != nil {
		t.Fatal(err)
	}
	if plan, _ = db.LoadPlan(ctx, planID); !store.PlanOpen(plan) {
		t.Fatal("stage cancelled within the grace")
	}
	if err := cancelStalledExcavation(ctx, db, goal, 7+excavationStallTicks); err != nil {
		t.Fatal(err)
	}
	plan, _ = db.LoadPlan(ctx, planID)
	for _, p := range plan.Progress {
		v := p.View()
		if v.Action == action && v.Stage != domain.Cancelled || v.Action != action && v.Stage != domain.Pending {
			t.Fatal(v.Action, v.Stage)
		}
	}
}
