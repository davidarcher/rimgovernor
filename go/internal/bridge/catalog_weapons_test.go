package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestWeaponOfReadsTheDefRows (#1723): the area weapons, the EMP and fire
// projectiles, the rockets and the blunt melee weapons come from the rows.
func TestWeaponOfReadsTheDefRows(t *testing.T) {
	catalog := FixtureCatalog("load", CoreWeaponFixtures()...)
	approx := func(got, want float64) bool { return got > want-0.001 && got < want+0.001 }
	for _, c := range []struct {
		def  string
		want policy.WeaponDef
	}{
		{"Weapon_GrenadeFrag", policy.WeaponDef{Ranged: true, Range: 12.9, Explosive: true, Blast: 1.9}},
		{"Weapon_GrenadeMolotov", policy.WeaponDef{Ranged: true, Range: 12.9, Explosive: true, Blast: 1.1, Incendiary: true}},
		{"Weapon_GrenadeEMP", policy.WeaponDef{Ranged: true, Range: 12.9, Explosive: true, Blast: 3.5, EMP: true}},
		{"Gun_EmpLauncher", policy.WeaponDef{Ranged: true, Range: 23.9, Explosive: true, Launcher: true, Blast: 1.1, EMP: true}},
		{"Gun_IncendiaryLauncher", policy.WeaponDef{Ranged: true, Range: 23.9, Explosive: true, Launcher: true, Blast: 1.1, Incendiary: true}},
		{"Gun_SmokeLauncher", policy.WeaponDef{Ranged: true, Range: 23.9, Explosive: true, Launcher: true}},
		{"Gun_TripleRocket", policy.WeaponDef{Ranged: true, Range: 35.9, Explosive: true, Launcher: true, OneUse: true}},
		{"Gun_AssaultRifle", policy.WeaponDef{Ranged: true, Range: 30.9}},
		{"MeleeWeapon_Club", policy.WeaponDef{Melee: true, Blunt: true}},
		{"MeleeWeapon_Warhammer", policy.WeaponDef{Melee: true, Blunt: true}},
		{"MeleeWeapon_Spear", policy.WeaponDef{Melee: true}},
		{"MeleeWeapon_Knife", policy.WeaponDef{Melee: true}},
		{"", policy.WeaponDef{}},
	} {
		got, err := catalog.WeaponOf(c.def)
		if err != nil {
			t.Fatalf("%s: %v", c.def, err)
		}
		if !approx(got.Range, c.want.Range) || !approx(got.Blast, c.want.Blast) {
			t.Fatalf("%s: got %+v, want %+v", c.def, got, c.want)
		}
		got.Range, got.Blast, c.want.Range, c.want.Blast = 0, 0, 0, 0
		if got != c.want {
			t.Fatalf("%s: got %+v, want %+v", c.def, got, c.want)
		}
	}
	if _, err := catalog.WeaponOf("Gun_Modded"); err == nil {
		t.Fatal("a weapon with no row was accepted")
	}
	var none *DefinitionCatalog
	if _, err := none.WeaponOf("Gun_AssaultRifle"); err == nil {
		t.Fatal("no catalog was accepted")
	}
}
