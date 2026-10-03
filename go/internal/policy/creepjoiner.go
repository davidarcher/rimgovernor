package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ManageCreepJoiners keeps a creepjoiner unarmed until its downside shows
// (#1740, epic #1694): the colonist drops the weapon it holds with the game's
// own drop job (domain.DropEquipment), and the equipment planner arms it only
// once the downside is revealed (EquipCandidatePawn.NoArms). A creepjoiner's
// downside is hidden at arrival, so an armed one is a risk until it shows.
const ManageCreepJoiners GoalID = "ManageCreepJoiners"

// CreepJoinerDownsides is what the game's creepjoiner downside defs add to
// the colonist who has one: the trait and hediff def names, over every
// downside def the catalog lists. A pawn showing one of them has a visible
// downside; which downside a particular creepjoiner has is hidden
// information and is never read. Empty without Anomaly.
type CreepJoinerDownsides struct {
	Traits, Hediffs map[string]bool
}

// CreepJoinerPawn is one colonist's facts for the downside check: its
// creepjoiner tracker facts and the def names of its traits and of its
// visible hediffs.
type CreepJoinerPawn struct {
	CreepJoiner     domain.Fact[*CreepJoiner]
	Traits, Hediffs domain.Fact[[]string]
}

// Revealed measures whether a creepjoiner's downside has shown: the game
// fired it (the tracker's triggered flag), or the pawn carries a trait or a
// visible hediff a downside def adds. Unknown while nothing shows and a fact
// the answer rests on is unread. The pawn must be a creepjoiner.
func (d CreepJoinerDownsides) Revealed(p CreepJoinerPawn) domain.Fact[bool] {
	joiner, _ := p.CreepJoiner.Value()
	if joiner == nil {
		return domain.Unknown[bool]()
	}
	if triggered, known := joiner.DownsideTriggered.Value(); known && triggered {
		return domain.Known(true)
	}
	traits, tk := p.Traits.Value()
	hediffs, hk := p.Hediffs.Value()
	if tk && slices.ContainsFunc(traits, func(t string) bool { return d.Traits[t] }) || hk && slices.ContainsFunc(hediffs, func(h string) bool { return d.Hediffs[h] }) {
		return domain.Known(true)
	}
	if _, known := joiner.DownsideTriggered.Value(); !known || !tk || !hk {
		return domain.Unknown[bool]()
	}
	return domain.Known(false)
}

// ArmsHold is the plain-English reason a colonist must not hold a weapon yet,
// "" when nothing holds it back: a creepjoiner whose downside has not shown,
// or one whose downside cannot be read. A pawn known to be no creepjoiner is
// never held.
func (d CreepJoinerDownsides) ArmsHold(p CreepJoinerPawn) string {
	joiner, known := p.CreepJoiner.Value()
	switch {
	case !known:
		return "its creepjoiner status could not be read, so it is not armed"
	case joiner == nil:
		return ""
	}
	if revealed, known := d.Revealed(p).Value(); !known {
		return "it is a creepjoiner whose downside could not be read, so it is not armed"
	} else if !revealed {
		return "it is a creepjoiner whose downside is not revealed yet, so it is not armed"
	}
	return ""
}

// CreepJoinerHand is one colonist's facts for the weapon drop and the
// inspection: the downside check's facts, whether the colonist can take an
// order now, the thing id of the weapon in its hands (known "" for none), its
// available surgical operations and its queued surgery bills.
type CreepJoinerHand struct {
	Pawn      PawnID
	Facts     CreepJoinerPawn
	Available domain.Fact[bool]
	Weapon    domain.Fact[string]
	// Operations, QueuedSurgeries and QueuedRecipes are the colonist's
	// native surgery facts (CarePawn's); QueuedRecipes is nil when unread.
	Operations      domain.Fact[[]SurgeryOperation]
	QueuedSurgeries domain.Fact[int]
	QueuedRecipes   []string
	// Area is the load id of the colonist's allowed area ("" when
	// unrestricted); Hungry is whether it is below the game's fed band.
	Area   domain.Fact[string]
	Hungry domain.Fact[bool]
}

// WeaponDrop is one colonist ordered to drop the weapon it holds.
type WeaponDrop struct {
	Pawn   PawnID
	Weapon string
}

// WeaponDrops are the drops the colony owes: every available colonist held
// back from arms who holds a weapon, sorted by pawn id. owed is known true
// with a drop, known false when none is owed and every held colonist was
// read, and unknown when none is owed but a held colonist's weapon or
// availability is unread.
func (d CreepJoinerDownsides) WeaponDrops(hands []CreepJoinerHand) (drops []WeaponDrop, owed domain.Fact[bool]) {
	unread := false
	for _, h := range hands {
		if d.ArmsHold(h.Facts) == "" {
			continue
		}
		weapon, wk := h.Weapon.Value()
		available, ak := h.Available.Value()
		switch {
		case !wk || !ak && weapon != "":
			unread = true
		case weapon != "" && available:
			drops = append(drops, WeaponDrop{Pawn: h.Pawn, Weapon: weapon})
		}
	}
	slices.SortFunc(drops, func(a, b WeaponDrop) int {
		switch {
		case a.Pawn < b.Pawn:
			return -1
		case a.Pawn > b.Pawn:
			return 1
		}
		return 0
	})
	switch {
	case len(drops) > 0:
		return drops, domain.Known(true)
	case unread:
		return nil, domain.Unknown[bool]()
	}
	return nil, domain.Known(false)
}
