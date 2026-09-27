package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SecureSuppliesHaulerFacts are the filters that select a candidate before
// a Haul action is proposed: a pawn that can take orders, an enabled,
// non-zero-priority Hauling work type and a healthy pawn (no needed tend, no
// bleeding). Native checks the chosen pawn and item again when it applies
// the intent. Hauling is the item the pawn's current haul job carries, empty
// when it has none.
type SecureSuppliesHaulerFacts struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	PlayerForced, NeedsTend, Bleeding  domain.Fact[bool]
	HaulingEnabled                     domain.Fact[bool]
	Hauling                            string
}

// HaulInTransit reports an item a pawn is already hauling: an applied haul
// completes its method when it is ordered, so the item stays listed until
// the hauler picks it up, and it is no target for another haul meanwhile.
func HaulInTransit(items []UpkeepItem, pawns []SecureSuppliesHaulerFacts) bool {
	for _, item := range items {
		if haulTaken(item.ID, pawns) {
			return true
		}
	}
	return false
}

func haulTaken(id string, pawns []SecureSuppliesHaulerFacts) bool {
	for _, p := range pawns {
		if p.Hauling != "" && p.Hauling == id {
			return true
		}
	}
	return false
}

// SelectSecureSupplies pairs the highest-priority vulnerable item (callers
// pass items already ordered the way ReviewUpkeep sorts them: medicine first,
// then soonest rot, then stable ID) with the lowest-ID eligible hauler. It is
// a proposal only; native owns whether a concrete storage destination exists
// and the job is actually taken. An item a pawn is already hauling is
// skipped.
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
	var open []UpkeepItem
	for _, item := range items {
		if !haulTaken(item.ID, pawns) {
			open = append(open, item)
		}
	}
	if len(pool) == 0 || len(open) == 0 {
		return UpkeepItem{}, "", false
	}
	sort.Slice(pool, func(i, j int) bool { return pool[i].Pawn < pool[j].Pawn })
	return open[0], pool[0].Pawn, true
}
