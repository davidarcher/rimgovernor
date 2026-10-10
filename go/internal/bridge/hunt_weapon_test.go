package bridge

import (
	"slices"
	"testing"
)

// TestHuntWeaponReadsTheDefRows is the parity check against the native hunt
// census this rule replaced (VerbProperties.range/warmupTime/ai_IsWeapon,
// defaultProjectile class and ProjectileProperties.explosionRadius/damageDef):
// over every vanilla weapon the recorded catalog holds, exactly the bows and the
// bullet guns hunt, and the grenades, launchers, rockets, flame and melee
// weapons do not.
func TestHuntWeaponReadsTheDefRows(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	var hunting, other []string
	for name, row := range catalog.ThingDefs {
		if len(row.GetVerbs()) == 0 {
			continue
		}
		weapon, err := catalog.HuntWeapon(name)
		if err != nil {
			continue
		}
		if weapon.Hunts() {
			hunting = append(hunting, name)
		} else if weapon.Ranged {
			other = append(other, name)
		}
	}
	for _, name := range []string{"Bow_Short", "Bow_Recurve", "Bow_Great", "Gun_Autopistol", "Gun_Revolver", "Gun_BoltActionRifle", "Gun_PumpShotgun", "Gun_AssaultRifle", "Gun_LMG", "Gun_SniperRifle"} {
		if !slices.Contains(hunting, name) {
			t.Errorf("%s does not hunt: %v", name, hunting)
		}
	}
	for _, name := range []string{"Weapon_GrenadeFrag", "Weapon_GrenadeMolotov", "Weapon_GrenadeEMP", "Gun_TripleRocket", "Gun_DoomsdayRocket", "Gun_IncendiaryLauncher", "Gun_Incinerator", "Flamebow", "Gun_BeamGraser"} {
		if slices.Contains(hunting, name) || !slices.Contains(other, name) {
			t.Errorf("%s must be a ranged weapon that does not hunt", name)
		}
	}
	weapon, err := catalog.HuntWeapon("MeleeWeapon_Knife")
	if err != nil || weapon.Hunts() || weapon.Ranged || !weapon.Melee {
		t.Errorf("a knife is a melee weapon, got %+v %v", weapon, err)
	}
	if weapon, err := catalog.HuntWeapon(""); weapon != nil || err != nil {
		t.Errorf("no weapon is unarmed, got %+v %v", weapon, err)
	}
	rifle, _ := catalog.HuntWeapon("Gun_BoltActionRifle")
	if len(rifle.Verbs) != 1 || rifle.Verbs[0].Range < 36.8 || rifle.Verbs[0].Range > 37 || rifle.Verbs[0].DamageDef != "Bullet" {
		t.Errorf("rifle verbs %+v", rifle.Verbs)
	}
}
