package policy

import "testing"

// The vanilla threat classification the named tables must keep: the inferno
// cannon's verb is explosive so it ranks with the rocketeers whatever the
// kind; the rest rank by kind or by the pila.
func TestThreatTierVanillaKindsAndWeapons(t *testing.T) {
	inferno := WeaponDef{Ranged: true, Range: 26.9, Explosive: true, Launcher: true, Incendiary: true, Blast: 2.4, ForcedMiss: true}
	for _, c := range []struct {
		name string
		h    CombatPawnState
		want int
	}{
		{"inferno centipede", CombatPawnState{Kind: "Mech_CentipedeBurner", Mech: true, Weapon: "Gun_InfernoCannon", WeaponFacts: inferno}, threatExplosive},
		{"centipede, weapon unread", CombatPawnState{Kind: "Mech_CentipedeBurner", Mech: true}, threatInfernoCentipede},
		{"scyther", CombatPawnState{Kind: "Mech_Scyther", Mech: true}, threatScyther},
		{"termite", CombatPawnState{Kind: "Mech_Termite_Breach", Mech: true}, threatTermite},
		{"lancer", CombatPawnState{Kind: "Mech_Lancer", Mech: true}, threatMech},
		{"berserker", CombatPawnState{Kind: "Tribal_Berserker", Weapon: "MeleeWeapon_Club"}, threatBerserker},
		{"pila thrower", CombatPawnState{Kind: "Tribal_Warrior", Weapon: "Pila"}, threatPila},
		{"berserker with a pila", CombatPawnState{Kind: "Tribal_Berserker", Weapon: "Pila"}, threatBerserker},
		{"archer", CombatPawnState{Kind: "Tribal_Archer", Weapon: "Bow_Recurve"}, threatOther},
	} {
		if got := threatTier(c.h, nil); got != c.want {
			t.Errorf("%s: tier %d, want %d", c.name, got, c.want)
		}
	}
}
