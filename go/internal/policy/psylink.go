package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainPsylink gives willing colonists a psylink (#1609, epic #1598): the
// psylink neuroformer is used by the colonist on itself (UseItem with the
// pawn as its own target). Acquiring the neuroformer is MaintainResource's:
// NeuroformerNeeds raises a stock floor of one, which the resource ladder
// meets with a production bill when the recipe is available and a trade buy
// when a trader sells it. Psyfocus is kept at target by the meditation
// schedule (PlanSchedulesHeld, #1316), not by this goal.
const MaintainPsylink GoalID = "MaintainPsylink"

// PsylinkNeuroformer is the ThingDef that grants a psylink (or a level) to
// the colonist who uses it; psycast neurotrainers teach one psycast and need
// a psylink first, so only this def serves a colonist without one.
const PsylinkNeuroformer = "PsychicAmplifier"

// PsylinkUse is one colonist using one neuroformer item on itself.
type PsylinkUse struct {
	Pawn PawnID
	Item string
}

// PsylinkCandidates are the colonists willing to take a psylink: available
// (alive, standing, undrafted, out of a mental state), read with a needs
// block, and with no psylink. A colonist's psylink facts are known only for
// a psycaster (WorkPawn.PsylinkLevel), so with the needs block read, an
// unknown level is none. Unknown without the royalty read or the pawn rows,
// and none when the game has no psylink neuroformer def. Sorted by id.
func PsylinkCandidates(royalty domain.Fact[RoyaltyFacts], pawns domain.Fact[[]WorkPawn]) domain.Fact[[]PawnID] {
	facts, rk := royalty.Value()
	rows, pk := pawns.Value()
	if !rk || !pk {
		return domain.Unknown[[]PawnID]()
	}
	out := []PawnID{}
	if _, ok := facts.Neuroformers[PsylinkNeuroformer]; !ok {
		return domain.Known(out)
	}
	for _, pawn := range rows {
		available, ak := pawn.Available.Value()
		_, nk := pawn.Rest.Value()
		_, lk := pawn.PsylinkLevel.Value()
		if ak && available && nk && !lk {
			out = append(out, pawn.ID)
		}
	}
	slices.Sort(out)
	return domain.Known(out)
}

// NeuroformerNeeds adds a stock floor of one psylink neuroformer to needs
// while a candidate waits, none is held and the game can supply one (an
// available recipe or a trader selling it). Unknown stock or candidates add
// nothing: the colony never buys against an unread census.
func NeuroformerNeeds(needs map[Resource]int64, royalty domain.Fact[RoyaltyFacts], candidates domain.Fact[[]PawnID]) map[Resource]int64 {
	facts, rk := royalty.Value()
	who, ck := candidates.Value()
	if !rk || !ck || len(who) == 0 {
		return needs
	}
	stock, ok := facts.Neuroformers[PsylinkNeuroformer]
	held, hk := stock.Held.Value()
	craftable, _ := stock.Craftable.Value()
	tradeable, _ := stock.Tradeable.Value()
	if !ok || !hk || held > 0 || !craftable && !tradeable {
		return needs
	}
	return ResourceGoalTargets(needs, map[Resource]int64{PsylinkNeuroformer: 1})
}

// NextPsylinkUse is the use the colony owes now: the first candidate with
// the first held neuroformer item. items are the unforbidden item ids a live
// read found; the stock count in the royalty read lags it. One use per call:
// a second candidate is served once the first colonist holds its psylink.
func NextPsylinkUse(candidates []PawnID, items []string) (PsylinkUse, bool) {
	if len(candidates) == 0 || len(items) == 0 {
		return PsylinkUse{}, false
	}
	first := slices.Min(candidates)
	return PsylinkUse{Pawn: first, Item: slices.Min(items)}, true
}

// PsylinkOwed measures MaintainPsylink: a use is owed while a candidate and a
// held neuroformer item both exist. Unknown while the candidates are unknown
// or a candidate waits on an unread item list; known false with no candidate.
func PsylinkOwed(candidates domain.Fact[[]PawnID], items domain.Fact[[]string]) domain.Fact[bool] {
	who, ck := candidates.Value()
	if !ck {
		return domain.Unknown[bool]()
	}
	if len(who) == 0 {
		return domain.Known(false)
	}
	held, ik := items.Value()
	if !ik {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(held) > 0)
}
