package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestAnimalNeedsRetainRiskAcrossManualRestartAndUnknown(t *testing.T) {
	path := filepath.Join(t.TempDir(), "animals.db")
	s := open(t, path)
	r := routineRequest()
	set := func(n float64, contained bool) {
		r.Facts.AnimalUpkeep = policy.AnimalUpkeepObservation{
			Animals: domain.Known([]policy.UpkeepAnimal{{ID: "animal", Definition: "Muffalo", RequiresPen: domain.Known(true), Contained: domain.Known(contained), Release: domain.Known(false), Slaughter: domain.Known(false)}}),
			Food:    domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "animal", NutritionPerDay: domain.Known(1.0)}}, Stocks: []policy.FoodStock{{ID: "hay", Holder: domain.Known(policy.PawnID("")), Nutrition: domain.Known(n), Eaters: []policy.PawnID{"animal"}, Perishable: domain.Known(false)}}}),
		}
	}
	set(1, false)
	out := reviewRoutine(t, s, &r)
	for _, id := range []policy.GoalID{policy.MaintainAnimalContainment, policy.MaintainAnimalFeed} {
		if g := routineGoal(t, out, id); g.Goal.Need != domain.NeedDeficit || g.Goal.Priority != 3 {
			t.Fatal(g)
		}
	}
	r.Facts.AnimalUpkeep = policy.AnimalUpkeepObservation{}
	out = reviewRoutine(t, s, &r)
	for _, id := range []policy.GoalID{policy.MaintainAnimalContainment, policy.MaintainAnimalFeed} {
		if g := routineGoal(t, out, id); g.Goal.Need != domain.NeedUnknown || g.Goal.Priority != 3 {
			t.Fatal(g)
		}
	}
	r.Enabled = false
	reviewRoutine(t, s, &r)
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	defer s.Close()
	retained, err := s.LoadRoutineReview(context.Background())
	if err != nil || retained.Enabled || !retained.Latches.Animals.Containment || len(retained.Latches.Animals.Feed) != 1 {
		t.Fatal(retained, err)
	}
	r.Enabled = true
	set(3, true)
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainAnimalFeed); g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
	if g := routineGoal(t, out, policy.MaintainAnimalContainment); g.Goal.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
	set(4, true)
	r.Facts.UpkeepIssued = map[policy.GoalID]bool{policy.MaintainAnimalFeed: true}
	out = reviewRoutine(t, s, &r)
	recovered := routineGoal(t, out, policy.MaintainAnimalFeed)
	if recovered.Goal.Need != domain.NeedRecovered || len(out.Review.Latches.Animals.Feed) != 0 {
		t.Fatal(recovered)
	}
	set(1, false)
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainAnimalFeed); g.Goal.Need != domain.NeedDeficit || g.Goal.Epoch <= recovered.Goal.Epoch {
		t.Fatal(g)
	}
	set(3, true)
	r.Current.Load = "replacement"
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainAnimalFeed); g.Goal.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
}
