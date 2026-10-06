package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// remoteSupplyLabor leaves remote loot and salvage unrationed: their holds
// are reach, threat, urgent work and demand, never the colonists' labor.
const remoteSupplyLabor = 1e12

// RemoteSupply is the supply plan's verdict over remote loot or salvage
// candidates: the ids it opened, best first, and the stable hold word of each
// it refused.
type RemoteSupply struct {
	Opened []string
	Held   map[string]string
}

// PlanRemoteSupply plans candidates against the request's stock demand with
// its urgent-work competition: the same PlanSupply that decides mines, bills
// and drills. Releasing or removing is not rationed by a deficit the better
// candidates already cover, so a candidate that only "target covered" holds is
// still admissible; the rest hold with a demand reason (`no_demand`,
// `no_storage_headroom`, `competing_urgent_work`, `unknown_demand_or_cost`).
func PlanRemoteSupply(r RemoteWorkRequest, candidates []AcquisitionCandidate) (RemoteSupply, error) {
	out := RemoteSupply{Held: map[string]string{}}
	if len(candidates) == 0 {
		return out, nil
	}
	rows, known := r.Demand.Value()
	var demands []SupplyDemand
	for _, d := range rows {
		demands = append(demands, SupplyDemandOfResource(d))
	}
	var supply []SupplyCandidate
	for _, c := range candidates {
		supply = append(supply, SupplyCandidateOfAcquisition(c))
	}
	demandFact := domain.Unknown[[]SupplyDemand]()
	if known {
		demandFact = domain.Known(demands)
	}
	plan, err := PlanSupply(SupplyPlanRequest{Demands: demandFact, Candidates: domain.Known(supply), Labor: domain.Known(float64(remoteSupplyLabor)), UrgentPriority: r.Competition.UrgentPriority})
	if err != nil {
		return out, err
	}
	for _, e := range plan.Portfolio {
		switch {
		case e.Decision == SupplyOpen:
			out.Opened = append(out.Opened, e.Candidate.ID)
		case e.Reason != "target covered":
			out.Held[e.Candidate.ID] = strings.ReplaceAll(e.Reason, " ", "_")
		}
	}
	for _, e := range plan.Unknown {
		out.Held[e.Candidate.ID] = "unknown_demand_or_cost"
	}
	return out, nil
}
