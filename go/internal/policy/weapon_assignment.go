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
	WeaponSwapGain                    = .2 // Require a twenty percent improvement over an owned primary.
)

// WeaponProfile contains nominal damage throughput, penetration and effective
// range. These are planning estimates, not a prediction of native combat damage.
type WeaponProfile struct {
	DPS, AP, Range        float64
	Precision, ForcedMiss bool
}

// Core weapon families; unknown definitions use conservative class defaults.
// Area fire is opt-in even when no role census has been supplied.
var weaponProfiles = map[string]WeaponProfile{
	"MeleeWeapon_Club":       {DPS: 6, AP: .18, Range: 1},
	"MeleeWeapon_Knife":      {DPS: 6, AP: .18, Range: 1},
	"MeleeWeapon_Gladius":    {DPS: 7, AP: .24, Range: 1},
	"MeleeWeapon_Longsword":  {DPS: 9, AP: .3, Range: 1},
	"MeleeWeapon_Mace":       {DPS: 8, AP: .3, Range: 1},
	"Gun_BoltActionRifle":    {DPS: 4, AP: .27, Range: 37, Precision: true},
	"Gun_SniperRifle":        {DPS: 4, AP: .38, Range: 45, Precision: true},
	"Gun_PumpShotgun":        {DPS: 8, AP: .14, Range: 16},
	"Gun_ChainShotgun":       {DPS: 12, AP: .14, Range: 13},
	"Gun_HeavySMG":           {DPS: 9, AP: .18, Range: 23},
	"Gun_MachinePistol":      {DPS: 7, AP: .09, Range: 20},
	"Gun_AssaultRifle":       {DPS: 8, AP: .16, Range: 31},
	"Gun_Revolver":           {DPS: 5, AP: .18, Range: 26},
	"Bow_Short":              {DPS: 3, AP: .11, Range: 23},
	"Bow_Recurve":            {DPS: 4, AP: .14, Range: 26},
	"Bow_Great":              {DPS: 5, AP: .2, Range: 30},
	"Gun_Minigun":            {DPS: 30, Range: 31, ForcedMiss: true},
	"Gun_LMG":                {DPS: 12, Range: 26, ForcedMiss: true},
	"Gun_IncendiaryLauncher": {DPS: 8, Range: 23, ForcedMiss: true},
	"Gun_SmokeLauncher":      {Range: 23, ForcedMiss: true},
	"Gun_EmpLauncher":        {Range: 23, ForcedMiss: true},
	"Gun_TripleRocket":       {DPS: 50, Range: 40, ForcedMiss: true},
	"Gun_DoomsdayRocket":     {DPS: 50, Range: 40, ForcedMiss: true},
	"Grenade_Frag":           {DPS: 20, Range: 13, ForcedMiss: true},
	"Grenade_EMP":            {Range: 13, ForcedMiss: true},
	"Grenade_Molotov":        {DPS: 8, Range: 13, ForcedMiss: true},
}

func ProfileWeapon(w EquipCandidateWeapon) WeaponProfile {
	if profile, ok := weaponProfiles[w.Definition]; ok {
		return profile
	}
	switch w.Class {
	case WeaponRanged:
		return WeaponProfile{DPS: 4, AP: .1, Range: 20}
	case WeaponMelee:
		return WeaponProfile{DPS: 6, AP: .2, Range: 1}
	default:
		return WeaponProfile{DPS: 1, Range: 1}
	}
}

// ScoreWeapon scores skill at an accuracy-weighted engagement range, then
// applies role fit and the known armor census. Unknown roles/armor are neutral.
func ScoreWeapon(p EquipCandidatePawn, w EquipCandidateWeapon) float64 {
	if p.Role == WeaponRoleNonCombatant {
		return 0
	}
	if w.BiocodedTo != "" && w.BiocodedTo != p.Pawn {
		return 0
	}
	if incapable, known := p.IncapableOfViolence.Value(); !known || incapable {
		return 0
	}
	profile := ProfileWeapon(w)
	if p.Role == WeaponRoleHunter && (w.Class != WeaponRanged || profile.Range < 25) {
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

// WeaponProductionDemand is the weapon input to the gear bill batch: one unit
// per still-unarmed pawn, net of the colony's loose-weapon assignment. Only
// discovered, researched recipes hosted by an available bench are considered;
// recipe ingredient funding and bill deduplication remain the production lane's.
func WeaponProductionDemand(pawns []EquipCandidatePawn, weapons []EquipCandidateWeapon, recipes []GearRecipe) []Amount {
	assigned := map[domain.PawnID]bool{}
	for _, pair := range AssignEquip(pawns, weapons) {
		assigned[pair.Pawn] = true
	}
	counts := map[Resource]int64{}
	for _, p := range pawns {
		armed, _ := p.Armed.Value()
		if assigned[p.Pawn] || armed || !equipAvailable(p) {
			continue
		}
		var best Resource
		bestScore := 0.0
		for _, recipe := range recipes {
			if !positive(recipe.Available) || !positive(recipe.AvailableOn) {
				continue
			}
			for _, def := range recipe.Products {
				profile, known := weaponProfiles[string(def)]
				if !known || profile.ForcedMiss {
					continue
				}
				class := WeaponRanged
				if profile.Range <= 1 {
					class = WeaponMelee
				}
				score := ScoreWeapon(p, EquipCandidateWeapon{Definition: string(def), Class: class})
				if score > bestScore || score > 0 && score == bestScore && def < best {
					best, bestScore = def, score
				}
			}
		}
		if best != "" {
			counts[best]++
			assigned[p.Pawn] = true
		}
	}
	var demand []Amount
	for def, count := range counts {
		demand = append(demand, Amount{Resource: def, Count: count})
	}
	sort.Slice(demand, func(i, j int) bool { return demand[i].Resource < demand[j].Resource })
	return demand
}

type EquipAssignment struct {
	Pawn   domain.PawnID
	Weapon EquipCandidateWeapon
	Score  float64
}

func equipAvailable(p EquipCandidatePawn) bool {
	for _, f := range []domain.Fact[bool]{p.Dead, p.Downed, p.Drafted, p.MentalState, p.IncapableOfViolence} {
		if value, known := f.Value(); !known || value {
			return false
		}
	}
	armed, known := p.Armed.Value()
	return known && (!armed || p.AutomationOwned && p.Current != nil)
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
		if !equipAvailable(p) || p.Current != nil && p.Current.BiocodedTo != "" {
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
			if score <= 0 || current > 0 && score <= current*(1+WeaponSwapGain) {
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
