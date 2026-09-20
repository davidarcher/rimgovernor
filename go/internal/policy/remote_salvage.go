package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// SalvageEvidence contains native output and route observations. Estimated
// yield never becomes inventory until ordinary pawn work delivers it.
type SalvageEvidence struct {
	Safe      domain.Fact[bool]
	Candidate AcquisitionCandidate
}

func SalvageContext(p RoutinePolicy, f RoutineFacts) (ResourceReachRequest, domain.Fact[[]ResourceDemand], error) {
	extent, err := DeriveColonyExtent(ColonyExtentRequest{Bounds: f.MapBounds, Construction: f.CurrentConstruction, Claims: f.ConstructionClaims, Stockpiles: f.OwnedStockpiles, Home: f.HomeCoverage})
	if err != nil {
		return ResourceReachRequest{}, domain.Unknown[[]ResourceDemand](), err
	}
	demand, err := LootDemand(p, f)
	return LootReach(f, f.MapBounds, extent), demand, err
}

// FilterRemoteSalvage preserves Home clearance and admits at most one remote
// removal. The next removal needs a new counterfactual roof-support census.
func FilterRemoteSalvage(rows []ClearanceTarget, reach ResourceReachRequest, demand domain.Fact[[]ResourceDemand]) ([]ClearanceTarget, []ClearanceHold, error) {
	out := append([]ClearanceTarget(nil), rows...)
	var candidates []AcquisitionCandidate
	var holds []ClearanceHold
	for i := range out {
		row := &out[i]
		row.SalvageSelected = false
		if row.InHome {
			continue
		}
		check := *row
		check.SalvageSelected = true
		reason := ClearanceHoldReason(check)
		if reason == "" && row.Salvage == nil {
			reason = "salvage_unknown"
		}
		if reason == "" {
			r := reach
			var headroom int64
			for _, y := range row.Salvage.Candidate.Yields {
				if n, ok := y.Headroom.Value(); ok {
					headroom = max(headroom, n)
				}
			}
			r.StorageHeadroom = domain.Known(headroom)
			decision := FilterResourceReach(r, ResourceReachCandidate{Cell: row.Minimum, Eligible: row.Salvage.Safe, RouteObservedPassable: row.Salvage.Safe})
			if !decision.Allowed {
				reason = decision.Reason
			}
		}
		if reason != "" {
			holds = append(holds, ClearanceHold{row.EntityID, reason})
			continue
		}
		candidate := row.Salvage.Candidate
		candidate.ID, candidate.Kind = row.EntityID, AcquisitionSalvage
		score, err := ScoreResourceCandidate(demand, candidate, AcquisitionCompetition{})
		if err != nil {
			return nil, nil, err
		}
		if score.Score <= 0 {
			holds = append(holds, ClearanceHold{row.EntityID, "demand:" + score.Hold})
			continue
		}
		candidates = append(candidates, candidate)
	}
	ranked, err := RankResourceCandidates(demand, candidates, AcquisitionCompetition{})
	if err != nil {
		return nil, nil, err
	}
	if len(ranked) > 0 {
		for i := range out {
			out[i].SalvageSelected = out[i].EntityID == ranked[0].ID
		}
	}
	return out, holds, nil
}
