package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CleanCandidateFacts mirrors SecureSuppliesHaulerFacts' eligibility inputs,
// substituting a Cleaning work-type gate for Hauling: Cleaning not disabled
// (priority may be 0: the order is player-forced and the bounded response
// targets exactly the colonies whose priority coverage failed) and a healthy
// pawn (no needed tend, no bleeding). EvaluateClean re-validates the exact chosen
// pawn/filth pair again immediately before dispatch; this only narrows which
// already-selected filth and pawn become one Clean proposal.
type CleanCandidateFacts struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	PlayerForced, NeedsTend, Bleeding  domain.Fact[bool]
	CleaningEnabled                    domain.Fact[bool]
}

// SelectClean pairs the highest-priority filth (callers pass filth already
// ordered the way ReviewUpkeep sorts it: critical rooms first, then stable
// ID) with the lowest-ID eligible cleaner. It is a proposal only; the native
// preview at dispatch still owns whether the job is actually accepted.
func SelectClean(filth []UpkeepFilth, pawns []CleanCandidateFacts) (UpkeepFilth, domain.PawnID, bool) {
	eligible := func(p CleanCandidateFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		drafted, tk := p.Drafted.Value()
		mental, mk := p.MentalState.Value()
		forced, fk := p.PlayerForced.Value()
		needsTend, nk := p.NeedsTend.Value()
		bleeding, bk := p.Bleeding.Value()
		cleaning, ck := p.CleaningEnabled.Value()
		if !dk || !wk || !tk || !mk || !fk || !nk || !bk || !ck {
			return false
		}
		return !dead && !downed && !drafted && !mental && !forced && !needsTend && !bleeding && cleaning
	}
	var pool []CleanCandidateFacts
	for _, p := range pawns {
		if eligible(p) {
			pool = append(pool, p)
		}
	}
	if len(pool) == 0 || len(filth) == 0 {
		return UpkeepFilth{}, "", false
	}
	sort.Slice(pool, func(i, j int) bool { return pool[i].Pawn < pool[j].Pawn })
	return filth[0], pool[0].Pawn, true
}
