package buildingruntime

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
)

// Everything the planners did not assign is pruned, per database; a database
// with nothing to drop yields no prune.
func TestPolicyPrunesDropsUnassigned(t *testing.T) {
	p := observation.Policies{
		Outfit:       []observation.PolicyEntry{{ID: "ApparelPolicy_1", Label: "Anything", Default: true}, {ID: "ApparelPolicy_7", Label: "Tynan"}},
		Drug:         []observation.PolicyEntry{{ID: "DrugPolicy_2", Label: "Tynan"}},
		Reading:      []observation.PolicyEntry{{ID: "ReadingPolicy_1"}, {ID: "ReadingPolicy_2"}},
		AllowedAreas: []observation.AllowedArea{{ID: "Area_4", Label: "Area 1"}, {ID: "Area_9", Label: "Tynan"}},
	}
	keep := map[string]bool{"ApparelPolicy_7": true, "DrugPolicy_2": true, "Area_9": true}
	got := policyPrunes(p, keep)
	want := map[domain.PolicyDatabase][]string{
		domain.OutfitPolicies:  {"ApparelPolicy_1"},
		domain.ReadingPolicies: {"ReadingPolicy_1", "ReadingPolicy_2"},
		domain.AllowedAreas:    {"Area_4"},
	}
	if len(got) != len(want) {
		t.Fatal(got)
	}
	for _, v := range got {
		if !slices.Equal(v.IDs(), want[v.Database()]) {
			t.Fatal(v.Database(), v.IDs())
		}
	}
	if len(policyPrunes(observation.Policies{}, nil)) != 0 {
		t.Fatal("empty databases pruned")
	}
}
