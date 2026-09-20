package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// SalvageEvidence contains native output and route observations. Estimated
// yield never becomes inventory until ordinary pawn work delivers it.
type SalvageEvidence struct {
	Safe      domain.Fact[bool]
	Candidate AcquisitionCandidate
}

// SalvageContext assembles the remote request every remote selection in a
// review shares: reach from the derived extent, demand from the effective
// targets and the urgent work competing for the colonists.
func SalvageContext(p RoutinePolicy, f RoutineFacts) (RemoteWorkRequest, error) {
	extent, err := DeriveColonyExtent(ColonyExtentRequest{Bounds: f.MapBounds, Construction: f.CurrentConstruction, Claims: f.ConstructionClaims, Stockpiles: f.OwnedStockpiles, Home: f.HomeCoverage})
	if err != nil {
		return RemoteWorkRequest{}, err
	}
	demand, err := LootDemand(p, f)
	return RemoteWorkRequest{Reach: LootReach(f, f.MapBounds, extent), Demand: demand, Competition: RemoteCompetition(f)}, err
}

// FilterRemoteSalvage preserves Home clearance and admits at most one remote
// removal. The next removal needs a new counterfactual roof-support census.
// Holds carry the explicit remote reasons (RemoteHoldReason): a known threat
// first, then roof support, native route safety, the reach stage and demand.
func FilterRemoteSalvage(rows []ClearanceTarget, r RemoteWorkRequest) ([]ClearanceTarget, []ClearanceHold, error) {
	reach, demand := r.Reach, r.Demand
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
		reason := remoteThreatHold(reach)
		if reason == "" {
			reason = RemoteHoldReason(RemoteSalvage, ClearanceHoldReason(check))
		}
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
				reason = RemoteHoldReason(RemoteSalvage, decision.Reason)
			}
		}
		if reason != "" {
			holds = append(holds, ClearanceHold{row.EntityID, reason})
			continue
		}
		candidate := row.Salvage.Candidate
		candidate.ID, candidate.Kind = row.EntityID, AcquisitionSalvage
		score, err := ScoreResourceCandidate(demand, candidate, r.Competition)
		if err != nil {
			return nil, nil, err
		}
		if score.Score <= 0 {
			holds = append(holds, ClearanceHold{row.EntityID, RemoteHoldReason(RemoteSalvage, "demand:"+score.Hold)})
			continue
		}
		candidates = append(candidates, candidate)
	}
	ranked, err := RankResourceCandidates(demand, candidates, r.Competition)
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
