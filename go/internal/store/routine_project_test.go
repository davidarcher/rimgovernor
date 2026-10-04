package store

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A finished Project stays finished through an unknown measurement and,
// once broken, opens a new Project row instead of bumping its epoch (#1022).
func TestRoutineProjectFinishesAndRegressionOpensNewRow(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Facts.Cooking = domain.Known(true)
	first := routineGoal(t, reviewRoutine(t, s, &r), policy.EnsureCooking)
	if !domain.ProjectGoalFinished(first.Goal) {
		t.Fatal(first)
	}
	r.Facts.Cooking = domain.Unknown[bool]()
	if g := routineGoal(t, reviewRoutine(t, s, &r), policy.EnsureCooking); g.Goal.ID != first.Goal.ID || !domain.ProjectGoalFinished(g.Goal) {
		t.Fatal("unknown reopened a finished project", g)
	}
	r.Facts.Cooking = domain.Known(false)
	next := routineGoal(t, reviewRoutine(t, s, &r), policy.EnsureCooking)
	if next.Goal.ID == first.Goal.ID || next.Goal.Epoch != 0 || next.Goal.Status != domain.GoalActive || next.Goal.Need != domain.NeedDeficit {
		t.Fatal(next)
	}
	old, err := s.LoadGoal(t.Context(), first.Goal.ID)
	if err != nil || !domain.ProjectGoalFinished(old.Goal) {
		t.Fatal("finished record lost", old, err)
	}
}
