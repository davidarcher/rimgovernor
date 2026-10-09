package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestWeaponOfReadsTheDefRows: the area weapons, the EMP and fire
// projectiles, the rockets and the blunt melee weapons come from the rows.
func TestWeaponOfReadsTheDefRows(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	approx := func(got, want float64) bool { return got > want-0.001 && got < want+0.001 }
	for _, c := range []struct {
		def  string
		want policy.WeaponDef
	}{
		{"Weapon_GrenadeFrag", policy.WeaponDef{ByTrade: true, Ranged: true, Range: 12.9, Reach: 12.9, Explosive: true, Blast: 1.9}},
		{"Weapon_GrenadeMolotov", policy.WeaponDef{ByTrade: true, Ranged: true, Range: 12.9, Reach: 12.9, Explosive: true, Blast: 1.1, Incendiary: true}},
		{"Weapon_GrenadeEMP", policy.WeaponDef{ByTrade: true, Ranged: true, Range: 12.9, Reach: 12.9, Explosive: true, Blast: 3.5, EMP: true}},
		{"Gun_EmpLauncher", policy.WeaponDef{ByTrade: true, Ranged: true, Range: 23.9, Reach: 23.9, Explosive: true, Launcher: true, Blast: 1.1, EMP: true}},
		{"Gun_IncendiaryLauncher", policy.WeaponDef{ByTrade: true, Ranged: true, Range: 23.9, Reach: 23.9, Explosive: true, Launcher: true, Blast: 1.1, Incendiary: true}},
		{"Gun_SmokeLauncher", policy.WeaponDef{ByTrade: true, Ranged: true, Range: 23.9, Reach: 23.9, Explosive: true, Launcher: true}},
		{"Gun_TripleRocket", policy.WeaponDef{ByTrade: true, Ranged: true, Range: 35.9, Reach: 35.9, Explosive: true, Launcher: true, OneUse: true}},
		{"Gun_AssaultRifle", policy.WeaponDef{ByTrade: true, Ranged: true, Range: 30.9, Reach: 30.9}},
		{"MeleeWeapon_Club", policy.WeaponDef{ByTrade: true, Melee: true, Reach: 1.42, Blunt: true}},
		{"MeleeWeapon_Warhammer", policy.WeaponDef{ByTrade: true, Melee: true, Reach: 1.42, Blunt: true}},
		{"MeleeWeapon_Spear", policy.WeaponDef{ByTrade: true, Melee: true, Reach: 1.42}},
		{"MeleeWeapon_Knife", policy.WeaponDef{ByTrade: true, Melee: true, Reach: 1.42}},
		{"WoodLog", policy.WeaponDef{Melee: true, Reach: 1.42, Blunt: true}},
		{"", policy.WeaponDef{}},
	} {
		got, err := catalog.WeaponOf(c.def)
		if err != nil {
			t.Fatalf("%s: %v", c.def, err)
		}
		if !approx(got.Range, c.want.Range) || !approx(got.Blast, c.want.Blast) || !approx(got.Reach, c.want.Reach) {
			t.Fatalf("%s: got %+v, want %+v", c.def, got, c.want)
		}
		got.Range, got.Blast, c.want.Range, c.want.Blast, got.Reach, c.want.Reach = 0, 0, 0, 0, 0, 0
		got.DPS, got.AP, got.Precision, got.ForcedMiss = 0, 0, false, false
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

// TestWeaponOfThroughput: DPS, AP, Precision and ForcedMiss come from
// the verb, tool, projectile and stat rows. Before the rows, a table stated
// Minigun 30/0 (area fire), sniper 4/.38, club 6/.18, charge rifle 11/.35.
func TestWeaponOfThroughput(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	near := func(got, want float64) bool { return got > want-0.001 && got < want+0.001 }
	for _, c := range []struct {
		def        string
		dps, ap    float64
		precision  bool
		forcedMiss bool
	}{
		{"Gun_Minigun", 41.667, .15, true, false},
		{"Gun_SniperRifle", 5, .375, true, false},
		{"Gun_ChargeRifle", 14.118, .35, false, false},
		{"Gun_AssaultRifle", 10.879, .165, false, false},
		{"MeleeWeapon_Club", 7.487, .188, false, false},
		{"MeleeWeapon_Spear", 10.426, .381, false, false},
		{"Weapon_GrenadeFrag", 12.019, .1, false, true},
		{"Gun_SmokeLauncher", 0, 0, false, true},
	} {
		got, err := catalog.WeaponOf(c.def)
		if err != nil {
			t.Fatalf("%s: %v", c.def, err)
		}
		if !near(got.DPS, c.dps) || !near(got.AP, c.ap) || got.Precision != c.precision || got.ForcedMiss != c.forcedMiss {
			t.Errorf("%s: got dps %.3f ap %.3f precision %v forcedMiss %v, want %+v", c.def, got.DPS, got.AP, got.Precision, got.ForcedMiss, c)
		}
	}
}
