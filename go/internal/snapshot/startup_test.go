package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recorded from acceptance run shelter/excavation-round at 4528e874f
// (#745), every routine family on: the colony's first review (tick 15) and
// the first after its shelter recovered (tick 159570). They replace the
// startup/composed-* cases' planner decision (#655): shelter does not hold
// the colony alone. While the initial shelter is owed, the wood it is
// built from and the upkeep families open beside it, and once it recovers
// the upkeep goals stay open without it.
const (
	shelterOpen      = "testdata/startup-shelter-open.json.gz"
	shelterRecovered = "testdata/startup-shelter-recovered.json.gz"
)

func deficits(t *testing.T, path string) (Routine, map[policy.GoalID]bool) {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	needs, err := r.Detect()
	if err != nil {
		t.Fatal(err)
	}
	open := map[policy.GoalID]bool{}
	for _, a := range needs.Assessments {
		if a.Need == domain.NeedDeficit {
			open[a.ID] = true
		}
	}
	return r, open
}

func TestReplayShelterOpensBesideResourceAndUpkeep(t *testing.T) {
	t.Parallel()
	r, open := deficits(t, shelterOpen)
	for _, id := range []policy.GoalID{policy.MaintainHousing, policy.MaintainResource, policy.MaintainFoodStorage, policy.EnsureFoodSupply} {
		if !open[id] {
			t.Errorf("%s not open beside the initial shelter: %v", id, open)
		}
	}
	bound := map[policy.GoalID]bool{}
	for _, g := range r.Review.Goals {
		bound[g.Need] = true
	}
	if !bound[policy.MaintainHousing] || !bound[policy.MaintainResource] {
		t.Fatalf("the review did not bind shelter and resource goals together: %v", bound)
	}
}

func TestReplayUpkeepOutlivesTheRecoveredShelter(t *testing.T) {
	t.Parallel()
	r, open := deficits(t, shelterRecovered)
	if needs, err := r.Detect(); err != nil || needs.Latches.Housing == policy.HousingShelter {
		t.Fatal("the initial shelter is still owed after it recovered")
	}
	for _, id := range []policy.GoalID{policy.MaintainResource, policy.MaintainFoodStorage} {
		if !open[id] {
			t.Errorf("%s closed with the shelter: %v", id, open)
		}
	}
}

// A wood shortage does not retire the owed shelter (#758): with the
// recorded review's wood census emptied, the initial shelter stays open
// and MaintainResource opens beside it to chop the wood back, so the
// adopted shell holds (TestRoutineShelterHoldsThroughWoodShortage) rather
// than the goal closing and a second shell being sited once wood returns.
func TestReplayWoodShortageKeepsTheShelterOwed(t *testing.T) {
	t.Parallel()
	r, err := Load(shelterOpen)
	if err != nil {
		t.Fatal(err)
	}
	r.Facts.Wood = domain.Known(int64(0))
	resources, _ := r.Facts.Resources.Value()
	short := make([]policy.Amount, 0, len(resources))
	for _, a := range resources {
		if a.Resource != "WoodLog" {
			short = append(short, a)
		}
	}
	r.Facts.Resources = domain.Known(short)
	needs, err := r.Detect()
	if err != nil {
		t.Fatal(err)
	}
	open := map[policy.GoalID]bool{}
	for _, a := range needs.Assessments {
		open[a.ID] = a.Need == domain.NeedDeficit
	}
	if !open[policy.MaintainHousing] || !open[policy.MaintainResource] {
		t.Fatalf("wood shortage: shelter owed=%v resource open=%v", open[policy.MaintainHousing], open[policy.MaintainResource])
	}
}
