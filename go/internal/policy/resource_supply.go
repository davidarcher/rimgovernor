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
	Candidates []AcquisitionCandidate
	// HorizonDays is how far ahead the deficit is wanted: a candidate with a
	// longer lead (a field) is not eligible. Zero is wanted now.
	HorizonDays float64
	// Fields are candidates with a lead and a steady rate, which an
	// AcquisitionCandidate cannot express (FieldHarvestCandidate).
	Fields []SupplyCandidate
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
	seen := map[[2]string]bool{}
	for _, in := range inputs {
		if in.Deficit <= 0 {
			continue
		}
		demand := SupplyDemandOfResource(ResourceDemand{Key: ResourceKey{Def: in.Resource}, Count: in.Deficit, Priority: 1})
		demand.HorizonDays = in.HorizonDays
		demands = append(demands, demand)
		for _, c := range in.Fields {
			id := [2]string{string(c.Kind), c.ID}
			if !seen[id] {
				seen[id] = true
				candidates = append(candidates, c)
			}
		}
		for _, c := range in.Candidates {
			id := [2]string{string(c.Kind), c.ID}
			if seen[id] {
				continue
			}
			seen[id] = true
			candidates = append(candidates, SupplyCandidateOfAcquisition(c))
		}
	}
	plan, err := PlanSupply(SupplyPlanRequest{Demands: domain.Known(demands), Candidates: domain.Known(candidates), Labor: labor})
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
func (s ResourceSupply) OpenedIDs(resource Resource, kind AcquisitionKind) map[string]bool {
	ids := map[string]bool{}
	for _, e := range s.Opened(resource) {
		if e.Candidate.Kind == acquisitionCandidateKinds[kind] {
			ids[e.Candidate.ID] = true
		}
	}
	return ids
}
