package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ResourceSupplyInput is one unmet MaintainResource floor and the candidates
// that can cover it: mines, bench bills, chops, harvests, hunts, deep drills and
// a caravan's offers.
type ResourceSupplyInput struct {
	Resource   Resource
	Deficit    int64
	Candidates []SupplyCandidate
	// HorizonDays is how far ahead the deficit is wanted: a candidate with a
	// longer lead (a field) is not eligible. Zero is wanted now.
	HorizonDays float64
	// Usable is the stock the candidates' ingredient draws may spend (see
	// UsableIngredients).
	Usable []ResourceQuantity
}

// ResourceSupply is the Round's one supply plan over every resource deficit.
type ResourceSupply struct {
	Plan SupplyPlan
}

// PlanResourceSupply runs PlanSupply over the deficits as stock demands wanted
// now, sharing one labor budget (work ticks per day; what the food plan leaves
// of workers * 20000). A candidate listed under two resources keeps its first
// listing.
func PlanResourceSupply(inputs []ResourceSupplyInput, labor domain.Fact[float64]) (ResourceSupply, error) {
	var demands []SupplyDemand
	var candidates []SupplyCandidate
	usable := map[ResourceKey]int64{}
	var keys []ResourceKey
	seen := map[[2]string]bool{}
	for _, in := range inputs {
		if in.Deficit <= 0 {
			continue
		}
		demand := SupplyDemandOfResource(ResourceDemand{Key: ResourceKey{Def: in.Resource}, Count: in.Deficit, Priority: 1})
		demand.HorizonDays = in.HorizonDays
		demands = append(demands, demand)
		for _, q := range in.Usable {
			if have, ok := usable[q.Key]; !ok {
				keys = append(keys, q.Key)
				usable[q.Key] = q.Count
			} else {
				usable[q.Key] = min(have, q.Count)
			}
		}
		for _, c := range in.Candidates {
			id := [2]string{string(c.Kind), c.ID}
			if seen[id] {
				continue
			}
			seen[id] = true
			candidates = append(candidates, c)
		}
	}
	var spendable []ResourceQuantity
	for _, k := range keys {
		spendable = append(spendable, ResourceQuantity{Key: k, Count: usable[k]})
	}
	plan, err := PlanSupply(SupplyPlanRequest{Demands: domain.Known(demands), Candidates: domain.Known(candidates), Labor: labor, Usable: spendable})
	return ResourceSupply{Plan: plan}, err
}

// Opened lists the candidates the plan opened for resource, best first.
func (s ResourceSupply) Opened(resource Resource) []SupplyEntry {
	var out []SupplyEntry
	for _, e := range s.Plan.Portfolio {
		if e.Decision != SupplyOpen {
			continue
		}
		for _, y := range e.Candidate.Yields {
			if y.Good.Def == resource {
				out = append(out, e)
				break
			}
		}
	}
	return out
}

// OpenedIDs is the ids of the opened candidates of one kind for resource, in
// rank order.
func (s ResourceSupply) OpenedIDs(resource Resource, kind CandidateKind) map[string]bool {
	ids := map[string]bool{}
	for _, e := range s.Opened(resource) {
		if e.Candidate.Kind == kind {
			ids[e.Candidate.ID] = true
		}
	}
	return ids
}
