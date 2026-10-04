package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainPsylink gives willing colonists a psylink (#1609, epic #1598) and
// raises untitled psycasters below MaxPsylinkLevel from neuroformers already
// in stock (#1940, PsylinkLevelUps; Deserter route out of scope): the
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

// MaxPsylinkLevel is the highest psylink level a neuroformer raises: the
// PsychicAmplifier hediff's maxSeverity, which
// CompUseEffect_InstallImplant.CanBeUsedBy refuses at.
const MaxPsylinkLevel = 6

// PsylinkLevelUps are the colonists a neuroformer already in stock raises a
// level (#1940): available, read with a needs block, holding a psylink below
// MaxPsylinkLevel and with no royal title in any faction (the Empire title
// caps the psylink level, and exceeding it breaks the rules). The royalty
// read's holdings carry every free colonist's titles, so a colonist with no
// holding is untitled. Unknown without the royalty read or the pawn rows, and
// none when the game has no psylink neuroformer def. Sorted by id. These
// colonists never raise NeuroformerNeeds: only existing stock serves them.
func PsylinkLevelUps(royalty domain.Fact[RoyaltyFacts], pawns domain.Fact[[]WorkPawn]) domain.Fact[[]PawnID] {
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
		level, lk := pawn.PsylinkLevel.Value()
		if ak && available && nk && lk && level < MaxPsylinkLevel && !titled(facts, pawn.ID) {
			out = append(out, pawn.ID)
		}
	}
	slices.Sort(out)
	return domain.Known(out)
}

// titled reports whether the colonist holds a royal title with any faction.
func titled(facts RoyaltyFacts, pawn PawnID) bool {
	for _, h := range facts.Holders[pawn] {
		if h.Title != "" {
			return true
		}
	}
	return false
}

// NeuroformerNeeds adds a stock floor of one psylink neuroformer to needs
// while a candidate waits, none is held and the game can supply one (an
// available recipe or a trader selling it). Unknown stock or candidates add
// nothing: the colony never buys against an unread census. candidates are
// the first-psylink colonists only, never PsylinkLevelUps.
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

// NextPsylinkUse is the use the colony owes now: the first candidate (a
// colonist with no psylink yet) with the first held neuroformer item, else the
// first level-up colonist. items are the unforbidden item ids a live read
// found; the stock count in the royalty read lags it. One use per call: a
// second colonist is served once the first one's psylink read catches up.
func NextPsylinkUse(candidates, levelUps []PawnID, items []string) (PsylinkUse, bool) {
	if len(items) == 0 {
		return PsylinkUse{}, false
	}
	switch {
	case len(candidates) > 0:
		return PsylinkUse{Pawn: slices.Min(candidates), Item: slices.Min(items)}, true
	case len(levelUps) > 0:
		return PsylinkUse{Pawn: slices.Min(levelUps), Item: slices.Min(items)}, true
	}
	return PsylinkUse{}, false
}

// PsylinkOwed measures MaintainPsylink: a use is owed while a candidate or a
// level-up colonist and a held neuroformer item both exist. Unknown while the
// candidates or level-ups are unknown or a colonist waits on an unread item
// list; known false with no one waiting.
func PsylinkOwed(candidates, levelUps domain.Fact[[]PawnID], items domain.Fact[[]string]) domain.Fact[bool] {
	first, ck := candidates.Value()
	up, uk := levelUps.Value()
	if !ck || !uk {
		return domain.Unknown[bool]()
	}
	if len(first)+len(up) == 0 {
		return domain.Known(false)
	}
	held, ik := items.Value()
	if !ik {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(held) > 0)
}
