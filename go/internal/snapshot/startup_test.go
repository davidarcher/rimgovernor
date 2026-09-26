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
	for _, id := range []policy.GoalID{policy.EnsureInitialShelter, policy.MaintainResource, policy.MaintainFoodStorage, policy.EnsureFoodSupply, policy.MaintainSleeping} {
		if !open[id] {
			t.Errorf("%s not open beside the initial shelter: %v", id, open)
		}
	}
	bound := map[policy.GoalID]bool{}
	for _, g := range r.Review.Goals {
		bound[g.Need] = true
	}
	if !bound[policy.EnsureInitialShelter] || !bound[policy.MaintainResource] {
		t.Fatalf("the review did not bind shelter and resource goals together: %v", bound)
	}
}

func TestReplayUpkeepOutlivesTheRecoveredShelter(t *testing.T) {
	t.Parallel()
	_, open := deficits(t, shelterRecovered)
	if open[policy.EnsureInitialShelter] {
		t.Fatal("the initial shelter is still owed after it recovered")
	}
	for _, id := range []policy.GoalID{policy.MaintainResource, policy.MaintainFoodStorage} {
		if !open[id] {
			t.Errorf("%s closed with the shelter: %v", id, open)
		}
	}
}
