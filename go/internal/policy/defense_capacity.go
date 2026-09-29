package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// DefensePointsPerDPS converts observed combat strength (damage per second)
// into raid-point units. Vanilla prices a pirate gunner carrying an assault
// rifle (about 11 DPS on the wire) at 50-65 combat power, and an unarmed
// raider brawls near 2-3 melee DPS for 35-ish points, so about 5 points per
// DPS lines the colony's measure up with the storyteller's raid budget.
const DefensePointsPerDPS = 5.0

// DefenseCapacity is the colony's combat strength in raid-point units
// (#1188): each combat-ready colonist (living, not downed, capable of
// violence) adds melee power (MeleeDPS times health) plus ranged weapon
// DPS, and every powered turret adds its DPS. Every input is an observation
// from the wire; any unknown one a sum needs leaves the result unknown.
func DefenseCapacity(defenders domain.Fact[[]SquadDefenderFacts], turrets domain.Fact[[]DefenseTurretFacts]) domain.Fact[float64] {
	unknown := domain.Unknown[float64]()
	pawns, pk := defenders.Value()
	guns, tk := turrets.Value()
	if !pk || !tk {
		return unknown
	}
	strength := 0.0
	for _, d := range pawns {
		dead, dk := d.Dead.Value()
		downed, nk := d.Downed.Value()
		violent, vk := d.ViolenceCapable.Value()
		if !dk || !nk || !vk {
			return unknown
		}
		if dead || downed || !violent {
			continue
		}
		melee, mk := d.MeleePower.Value()
		ranged, rk := d.RangedDPS.Value()
		if !mk || !rk {
			return unknown
		}
		strength += melee + ranged
	}
	for _, t := range guns {
		powered, known := t.Powered.Value()
		if !known {
			return unknown
		}
		if !powered {
			continue
		}
		dps, known := t.DPS.Value()
		if !known {
			return unknown
		}
		strength += dps
	}
	return domain.Known(strength * DefensePointsPerDPS)
}
