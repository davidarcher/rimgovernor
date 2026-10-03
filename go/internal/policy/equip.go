package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// EquipCandidatePawn describes an observed colonist and optional combat facts.
// Selection is a proposal; native admission rechecks the exact pair.
type EquipCandidatePawn struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	IncapableOfViolence, Armed         domain.Fact[bool]
	Position                           domain.Cell
	Profile                            PawnProfile
	ShootingDisabled                   bool
	Role                               WeaponRole
	// LoneFighter must be explicitly known before assigning area-fire weapons.
	LoneFighter bool
	RaidArmor   domain.Fact[float64]
	// Current is the equipped primary: the bot owns every equipment decision.
	Current *EquipCandidateWeapon
	// NoArms is the plain-English reason the pawn must not hold a weapon
	// yet, "" when nothing holds it back (a creepjoiner whose downside has
	// not shown, #1740; CreepJoinerDownsides.ArmsHold). Every weapon decision
	// leaves such a pawn unarmed.
	NoArms string
}

// EquipCandidateWeapon describes one already-observed loose weapon.
type EquipCandidateWeapon struct {
	Thing, Definition string
	Cell              domain.Cell
	Class             WeaponClass
	BiocodedTo        domain.PawnID
	Biocoded          bool
}

// WeaponClass ranks a loose equippable by what it is for. The native
// "weapons" census is ThingDef.IsWeapon, which includes anything a pawn can
// swing (a wood log, a beer): colony-6 handed a colonist the wood log at its
// feet with two short bows a few cells away. The class is the definition's
// observed flags (#287), never its name: IsMeleeWeapon is true of every
// equippable that is not ranged, so a melee weapon by trade is one the
// Weapons thing category lists.
type WeaponClass int

const (
	// WeaponMakeshift: equippable but not a weapon by trade (WoodLog, Beer).
	WeaponMakeshift WeaponClass = iota
	// WeaponMelee: IsMeleeWeapon and in the Weapons category.
	WeaponMelee
	// WeaponRanged: IsRangedWeapon; what hunting needs.
	WeaponRanged
)

// ClassifyWeapon maps the observed definition flags onto a class.
func ClassifyWeapon(byTrade, ranged, melee bool) WeaponClass {
	switch {
	case ranged:
		return WeaponRanged
	case melee && byTrade:
		return WeaponMelee
	}
	return WeaponMakeshift
}

// SelectEquip returns the first pair from the colony-wide assignment.
// Call AssignEquip to dispatch the whole wave. Admission remains native.
func SelectEquip(pawns []EquipCandidatePawn, weapons []EquipCandidateWeapon) (domain.PawnID, EquipCandidateWeapon, bool) {
	pairs := AssignEquip(pawns, weapons)
	if len(pairs) == 0 {
		return "", EquipCandidateWeapon{}, false
	}
	return pairs[0].Pawn, pairs[0].Weapon, true
}

func distanceSquared(a, b domain.Cell) int64 {
	dx, dz := int64(a.X-b.X), int64(a.Z-b.Z)
	return dx*dx + dz*dz
}
