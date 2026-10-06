package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// WeaponRole is optional: an absent role falls back to skills and traits.
type WeaponRole string

const (
	WeaponRoleMelee        WeaponRole = "melee"
	WeaponRoleRanged       WeaponRole = "ranged"
	WeaponRoleSniper       WeaponRole = "sniper"
	WeaponRoleHunter       WeaponRole = "hunter"
	WeaponRoleSoldier      WeaponRole = "soldier"
	WeaponRoleNonCombatant WeaponRole = "non-combatant"
)

// ScoreWeapon scores skill at an accuracy-weighted engagement range, then
// applies role fit and the known armor census. Unknown roles/armor are neutral.
func ScoreWeapon(p EquipCandidatePawn, w EquipCandidateWeapon) float64 {
	if p.Role == WeaponRoleNonCombatant || p.NoArms != "" {
		return 0
	}
	if (w.Biocoded || w.BiocodedTo != "") && w.BiocodedTo != p.Pawn {
		return 0
	}
	if incapable, known := p.IncapableOfViolence.Value(); !known || incapable {
		return 0
	}
	// The weapon's def rows (#1723): a weapon the producer gave none (zero
	// Facts) has no damage and scores nothing.
	profile := w.Facts
	if p.Role == WeaponRoleHunter && (w.Class != WeaponRanged || !profile.Hunts()) {
		return 0
	}
	if profile.ForcedMiss && !p.LoneFighter {
		return 0
	}
	shooting := p.Profile.Skills["Shooting"]
	meleeOnly := p.Profile.Effects.MeleeOnly || p.ShootingDisabled || shooting.Disabled
	if w.Class == WeaponRanged && meleeOnly {
		return 0
	}
	level := float64(max(0, min(20, shooting.Level)))
	score := profile.DPS
	if w.Class == WeaponRanged {
		// Precision weapons reward the long engagement distance an accurate
		// pawn can exploit; short burst weapons remain useful to novices.
		accuracy := .35 + .0325*level
		if profile.Precision {
			accuracy *= accuracy * (1 + 3*level/20)
		}
		score *= accuracy * (1 + profile.Range/20*level/20)
		if p.Role == WeaponRoleRanged || p.Role == WeaponRoleSniper || p.Role == WeaponRoleHunter {
			score *= 1.3
		}
		if p.Role == WeaponRoleSniper && profile.Precision {
			score *= 1.3
		}
	} else {
		level = float64(max(0, min(20, p.Profile.Skills["Melee"].Level)))
		score *= .5 + level/40
		if meleeOnly || p.Role == WeaponRoleMelee {
			score *= 3
		} else {
			score *= .35
		}
	}
	if armor, known := p.RaidArmor.Value(); known && !math.IsNaN(armor) && !math.IsInf(armor, 0) {
		score *= 1 - .75*math.Min(1, math.Max(0, armor-profile.AP))
	}
	return score
}

// UnarmedFighters counts the available, fighting-capable unarmed pawns the
// loose weapons cannot arm: the colonists a weapon must be crafted for.
func UnarmedFighters(pawns []EquipCandidatePawn, weapons []EquipCandidateWeapon) int {
	assigned := map[domain.PawnID]bool{}
	for _, pair := range AssignEquip(pawns, weapons) {
		assigned[pair.Pawn] = true
	}
	n := 0
	for _, p := range pawns {
		if armed, _ := p.Armed.Value(); !armed && !assigned[p.Pawn] && equipAvailable(p) {
			n++
		}
	}
	return n
}

// WeaponRecipe reports whether a bench recipe makes a weapon the armory
// ladder places on a rung.
func WeaponRecipe(recipe GearRecipe) bool {
	for _, def := range recipe.Products {
		if _, known := ArmoryWeaponTier(def); known {
			return true
		}
	}
	return false
}

type EquipAssignment struct {
	Pawn   domain.PawnID
	Weapon EquipCandidateWeapon
	Score  float64
}

func equipAvailable(p EquipCandidatePawn) bool {
	if p.NoArms != "" {
		return false
	}
	for _, f := range []domain.Fact[bool]{p.Dead, p.Downed, p.Drafted, p.MentalState, p.IncapableOfViolence} {
		if value, known := f.Value(); !known || value {
			return false
		}
	}
	armed, known := p.Armed.Value()
	return known && (!armed || p.Current != nil)
}

// AssignEquip greedily assigns all pairs in descending score order, using
// stable pawn/weapon identities for ties. Each pawn and thing occurs once.
// Existing player assignments and biocoded primaries are never displaced.
func AssignEquip(pawns []EquipCandidatePawn, weapons []EquipCandidateWeapon) []EquipAssignment {
	type pair struct {
		EquipAssignment
		distance int64
	}
	var pairs []pair
	for _, p := range pawns {
		if !equipAvailable(p) || p.Current != nil && (p.Current.Biocoded || p.Current.BiocodedTo != "") {
			continue
		}
		current := 0.0
		if p.Current != nil {
			current = ScoreWeapon(p, *p.Current)
		}
		for _, w := range weapons {
			if w.Thing == "" || p.Current != nil && p.Current.Thing == w.Thing {
				continue
			}
			score := ScoreWeapon(p, w)
			if score <= 0 || current > 0 && score <= current {
				continue
			}
			pairs = append(pairs, pair{EquipAssignment{p.Pawn, w, score}, distanceSquared(p.Position, w.Cell)})
		}
	}
	sort.Slice(pairs, func(i, j int) bool {
		a, b := pairs[i], pairs[j]
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		if a.Pawn != b.Pawn {
			return a.Pawn < b.Pawn
		}
		if a.distance != b.distance {
			return a.distance < b.distance
		}
		return a.Weapon.Thing < b.Weapon.Thing
	})
	usedPawns, usedWeapons := map[domain.PawnID]bool{}, map[string]bool{}
	var out []EquipAssignment
	for _, p := range pairs {
		if usedPawns[p.Pawn] || usedWeapons[p.Weapon.Thing] {
			continue
		}
		usedPawns[p.Pawn], usedWeapons[p.Weapon.Thing] = true, true
		out = append(out, p.EquipAssignment)
	}
	return out
}
