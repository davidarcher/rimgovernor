package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SecureSuppliesHaulerFacts mirrors HaulPawnFacts' eligibility inputs plus the
// two additional Python filters that select a candidate before a Haul action
// is even proposed: an enabled, non-zero-priority Hauling work type and a
// healthy pawn (no needed tend, no bleeding). EvaluateHaul re-validates the
// exact chosen pawn/thing pair again immediately before dispatch; this only
// narrows which already-selected item and pawn become one Haul proposal.
type SecureSuppliesHaulerFacts struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	PlayerForced, NeedsTend, Bleeding  domain.Fact[bool]
	HaulingEnabled                     domain.Fact[bool]
}

// SelectSecureSupplies pairs the highest-priority vulnerable item (callers
// pass items already ordered the way ReviewUpkeep sorts them: medicine first,
// then soonest rot, then stable ID) with the lowest-ID eligible hauler. It is
// a proposal only; the native preview at dispatch still owns whether a
// concrete storage destination exists and the job is actually accepted.
func SelectSecureSupplies(items []UpkeepItem, pawns []SecureSuppliesHaulerFacts) (UpkeepItem, domain.PawnID, bool) {
	eligible := func(p SecureSuppliesHaulerFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		drafted, tk := p.Drafted.Value()
		mental, mk := p.MentalState.Value()
		forced, fk := p.PlayerForced.Value()
		needsTend, nk := p.NeedsTend.Value()
		bleeding, bk := p.Bleeding.Value()
		hauling, hk := p.HaulingEnabled.Value()
		if !dk || !wk || !tk || !mk || !fk || !nk || !bk || !hk {
			return false
		}
		return !dead && !downed && !drafted && !mental && !forced && !needsTend && !bleeding && hauling
	}
	var pool []SecureSuppliesHaulerFacts
	for _, p := range pawns {
		if eligible(p) {
			pool = append(pool, p)
		}
	}
	if len(pool) == 0 || len(items) == 0 {
		return UpkeepItem{}, "", false
	}
	sort.Slice(pool, func(i, j int) bool { return pool[i].Pawn < pool[j].Pawn })
	return items[0], pool[0].Pawn, true
}
