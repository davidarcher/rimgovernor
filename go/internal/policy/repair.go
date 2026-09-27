package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RepairCandidateFacts mirrors SecureSuppliesHaulerFacts' eligibility inputs,
// substituting the Construction work-type gate for Hauling:
// an enabled, non-zero-priority Construction work type and a healthy pawn (no
// needed tend, no bleeding). EvaluateRepair re-validates the exact chosen
// pawn/structure pair again immediately before dispatch; this only narrows
// which already-selected structure and pawn become one Repair proposal.
type RepairCandidateFacts struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	PlayerForced, NeedsTend, Bleeding  domain.Fact[bool]
	ConstructionEnabled                domain.Fact[bool]
}

// SelectRepair pairs the highest-priority damaged structure (callers pass
// structures already ordered the way ReviewUpkeep sorts them: repair
// priority first, then lowest health fraction, then stable ID) with the
// lowest-ID eligible repairer. It is a proposal only; the native preview at
// dispatch still owns whether the job is actually accepted.
func SelectRepair(structures []UpkeepStructure, pawns []RepairCandidateFacts) (UpkeepStructure, domain.PawnID, bool) {
	eligible := func(p RepairCandidateFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		drafted, tk := p.Drafted.Value()
		mental, mk := p.MentalState.Value()
		needsTend, nk := p.NeedsTend.Value()
		bleeding, bk := p.Bleeding.Value()
		construction, ck := p.ConstructionEnabled.Value()
		if !dk || !wk || !tk || !mk || !nk || !bk || !ck {
			return false
		}
		return !dead && !downed && !drafted && !mental && !needsTend && !bleeding && construction
	}
	var pool []RepairCandidateFacts
	for _, p := range pawns {
		if eligible(p) {
			pool = append(pool, p)
		}
	}
	if len(pool) == 0 || len(structures) == 0 {
		return UpkeepStructure{}, "", false
	}
	sort.Slice(pool, func(i, j int) bool {
		a, b := orderedWorkCost(pool[i].PlayerForced), orderedWorkCost(pool[j].PlayerForced)
		if a != b {
			return a < b
		}
		return pool[i].Pawn < pool[j].Pawn
	})
	return structures[0], pool[0].Pawn, true
}
