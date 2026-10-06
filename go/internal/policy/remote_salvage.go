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
func SalvageContext(p RoundsPolicy, f RoundsFacts) (RemoteWorkRequest, error) {
	extent, err := DeriveColonyExtent(ColonyExtentRequest{Bounds: f.MapBounds, Construction: f.CurrentConstruction, Claims: f.ConstructionClaims, Home: f.HomeCoverage})
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
	reach := r.Reach
	out := append([]ClearanceTarget(nil), rows...)
	reasons := make([]string, len(out))
	var candidates []AcquisitionCandidate
	var remote []int
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
		if reasons[i] = reason; reason == "" {
			candidate := row.Salvage.Candidate
			candidate.ID, candidate.Kind = row.EntityID, AcquisitionSalvage
			candidates = append(candidates, candidate)
			remote = append(remote, i)
		}
	}
	supply, err := PlanRemoteSupply(r, candidates)
	if err != nil {
		return nil, nil, err
	}
	for j, i := range remote {
		if reason, held := supply.Held[candidates[j].ID]; held {
			reasons[i] = RemoteHoldReason(RemoteSalvage, "demand:"+reason)
		}
	}
	var holds []ClearanceHold
	for i, reason := range reasons {
		if reason != "" {
			holds = append(holds, ClearanceHold{out[i].EntityID, reason})
		}
	}
	if len(supply.Opened) > 0 {
		for i := range out {
			out[i].SalvageSelected = out[i].EntityID == supply.Opened[0]
		}
	}
	return out, holds, nil
}

// ReviewClearanceHolds is the rounds's journalled clearance
// judgement over a known clearance census: every Home hold, with a remote
// salvage hold replacing the Home reason for the same target, and the
// selected salvage target ("" when none).
func ReviewClearanceHolds(p RoundsPolicy, f RoundsFacts, rows []ClearanceTarget) ([]ClearanceHold, string, error) {
	holds := SelectHomeClearance(rows, domain.Cell{}).Holds
	remote, err := SalvageContext(p, f)
	if err != nil {
		return nil, "", err
	}
	filtered, remoteHolds, err := FilterRemoteSalvage(rows, remote)
	if err != nil {
		return nil, "", err
	}
	salvage := ""
	for _, row := range filtered {
		if row.SalvageSelected {
			salvage = row.EntityID
		}
	}
	for _, hold := range remoteHolds {
		for i := range holds {
			if holds[i].Target == hold.Target {
				holds[i] = hold
			}
		}
	}
	return holds, salvage, nil
}
