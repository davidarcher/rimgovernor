package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestAnimalNeedsRetainRiskAcrossManualRestartAndUnknown(t *testing.T) {
	t.Parallel()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	set := func(n float64, contained bool) {
		r.Facts.AnimalUpkeep = policy.AnimalUpkeepObservation{
			Animals: domain.Known([]policy.UpkeepAnimal{{ID: "animal", Definition: "Muffalo", RequiresPen: domain.Known(true), Contained: domain.Known(contained), Release: domain.Known(false), Slaughter: domain.Known(false)}}),
			Food:    domain.Known(policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "animal", NutritionPerDay: domain.Known(1.0)}}, Stocks: []policy.FoodStock{{ID: "hay", DefName: "Hay", Holder: domain.Known(policy.PawnID("")), Count: domain.Known(int64(10)), Nutrition: domain.Known(n), Eaters: []policy.PawnID{"animal"}, Perishable: domain.Known(false)}}}),
		}
	}
	set(1, false)
	out := reviewRounds(t, s, &r)
	for _, id := range []policy.ConcernID{policy.MaintainAnimalContainment, policy.MaintainAnimalFeed} {
		if g := roundsGoal(t, out, id); g.Standard.Finding != domain.FindingUnmet || g.Standard.Priority != 3 {
			t.Fatal(g)
		}
	}
	r.Facts.AnimalUpkeep = policy.AnimalUpkeepObservation{}
	out = reviewRounds(t, s, &r)
	// Containment keeps its latch across an unknown census; the feed reserve
	// has none, so an unread herd ranks as any other unknown optional need.
	for id, priority := range map[policy.ConcernID]int{policy.MaintainAnimalContainment: 3, policy.MaintainAnimalFeed: 4} {
		if g := roundsGoal(t, out, id); g.Standard.Finding != domain.FindingUnclear || g.Standard.Priority != priority {
			t.Fatal(g)
		}
	}
	r.Enabled = false
	reviewRounds(t, s, &r)
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	defer s.Close()
	retained, err := s.LoadRounds(context.Background())
	if err != nil || retained.Enabled || !retained.Latches.Animals.Containment {
		t.Fatal(retained, err)
	}
	r.Enabled = true
	set(3, true)
	out = reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainAnimalFeed); g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	if g := roundsGoal(t, out, policy.MaintainAnimalContainment); g.Standard.Finding != domain.FindingMet {
		t.Fatal(g)
	}
	set(5, true)
	r.Facts.UpkeepIssued = map[policy.ConcernID]bool{policy.MaintainAnimalFeed: true}
	out = reviewRounds(t, s, &r)
	recovered := roundsGoal(t, out, policy.MaintainAnimalFeed)
	if recovered.Standard.Finding != domain.FindingMet {
		t.Fatal(recovered)
	}
	set(1, false)
	out = reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainAnimalFeed); g.Standard.Finding != domain.FindingUnmet || g.Standard.Episode <= recovered.Standard.Episode {
		t.Fatal(g)
	}
	set(6, true)
	r.Current.Load = "replacement"
	out = reviewRounds(t, s, &r)
	if g := roundsGoal(t, out, policy.MaintainAnimalFeed); g.Standard.Finding != domain.FindingMet {
		t.Fatal(g)
	}
}
