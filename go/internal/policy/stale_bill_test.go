package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A Met owner with a stale bill is filed Unmet and raised so its planner
// removes the bill (#2411); an Unmet or unknown owner, or another owner's
// stale bill, changes nothing.
func TestAssessFilesAMetOwnerWithAStaleBillUnmet(t *testing.T) {
	t.Parallel()
	stale := []StaleBill{{Owner: MaintainArt, Bench: "bench", ID: "Bill_1"}}
	cases := []struct {
		name      string
		bills     []StaleBill
		recovered domain.Fact[bool]
		finding   domain.Finding
		raised    int
	}{
		{"met with a stale bill", stale, domain.Known(true), domain.FindingUnmet, 1},
		{"met without one", nil, domain.Known(true), domain.FindingMet, 0},
		{"met, another owner's bill", []StaleBill{{Owner: MaintainEquipment, ID: "Bill_2"}}, domain.Known(true), domain.FindingMet, 0},
		{"unmet", stale, domain.Known(false), domain.FindingUnmet, 0},
		{"unclear", stale, domain.Unknown[bool](), domain.FindingUnclear, 0},
	}
	for _, tc := range cases {
		c := &roundsRun{f: RoundsFacts{StaleBills: tc.bills}}
		a := c.assess(MaintainArt, 3, tc.recovered)
		if a.Finding != tc.finding || a.StaleBill != (tc.raised == 1) || len(c.r.Concerns) != tc.raised {
			t.Errorf("%s: finding %v stale %v concerns %d", tc.name, a.Finding, a.StaleBill, len(c.r.Concerns))
		}
	}
}

// The owners whose planners remove a stale bill exclude the gestation and
// food owners (#2411).
func TestStaleBillOwnersExcludeMechsAndFood(t *testing.T) {
	t.Parallel()
	for _, id := range []ConcernID{MaintainMechs, EnsureFoodSupply, MaintainFoodStorage} {
		if StaleBillOwner(id) {
			t.Errorf("%s removes stale bills", id)
		}
	}
	for _, id := range []ConcernID{MaintainEquipment, MaintainArt, MaintainSurgery} {
		if !StaleBillOwner(id) {
			t.Errorf("%s does not remove stale bills", id)
		}
	}
}
