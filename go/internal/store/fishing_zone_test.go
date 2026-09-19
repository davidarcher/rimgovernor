package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestFishingZoneMethodRoundTrip(t *testing.T) {
	for _, extend := range []bool{false, true} {
		s := open(t, memoryPath(t))
		r := foodDeficitRoutineRequest()
		goal := routineGoal(t, reviewRoutine(t, s, &r), policy.EnsureFoodSupply)
		z, err := domain.NewFishingZone([]domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}})
		if extend {
			z, err = domain.NewFishingZoneExtension("Zone_5", z.Cells())
		}
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewZoneCreateAction("fish-a", z)
		if err != nil {
			t.Fatal(err)
		}
		plan, err := domain.NewPlan("fish", 1, []domain.Action{a})
		if err != nil {
			t.Fatal(err)
		}
		if _, err = s.CommitGoalMethod(context.Background(), goal.Goal.ID, goal.Revision, "fishing", plan); err != nil {
			t.Fatal(err)
		}
		loaded, err := s.LoadPlan(context.Background(), "fish")
		if err != nil {
			t.Fatal(err)
		}
		actual, ok := loaded.Spec.Actions()[0].ZoneCreate()
		if !ok || actual != z {
			t.Fatal("lost fishing configuration", actual, z)
		}
	}
}
