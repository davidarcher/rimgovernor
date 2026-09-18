package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// GoalState.Admitted counts every method the goal ever committed, so a
// planner that salts its method identity with it never rehashes to a
// retired plan's id once the active method list shrinks (#214).
func TestLoadGoalAdmittedCountsRetiredMethods(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoutineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodSupply)
	if g.Admitted != 0 || len(g.Methods) != 0 {
		t.Fatalf("fresh goal: admitted=%d methods=%d", g.Admitted, len(g.Methods))
	}
	g, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-1", acquisitionPlan(t, "acquire-plan-1", "WoodLog"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("UPDATE plans SET retired=1 WHERE id='acquire-plan-1'"); err != nil {
		t.Fatal(err)
	}
	g, err = s.LoadGoal(ctx, g.Goal.ID)
	if err != nil {
		t.Fatal(err)
	}
	if len(g.Methods) != 0 || g.Admitted != 1 {
		t.Fatalf("after retirement: admitted=%d methods=%v", g.Admitted, g.Methods)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-2", acquisitionPlan(t, "acquire-plan-2", "WoodLog")); err != nil {
		t.Fatal(err)
	}
	if g, err = s.LoadGoal(ctx, g.Goal.ID); err != nil {
		t.Fatal(err)
	}
	if len(g.Methods) != 1 || g.Admitted != 2 || g.Methods[0].Plan != domain.PlanID("acquire-plan-2") {
		t.Fatalf("second method: admitted=%d methods=%v", g.Admitted, g.Methods)
	}
}
