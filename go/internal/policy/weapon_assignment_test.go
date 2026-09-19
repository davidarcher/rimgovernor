package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func weaponPawn(id domain.PawnID, shooting int) EquipCandidatePawn {
	return EquipCandidatePawn{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false), Profile: PawnProfile{Skills: map[string]ProfileSkill{"Shooting": {Level: shooting}, "Melee": {Level: 10}}}}
}

func TestWeaponAssignmentSkillTable(t *testing.T) {
	for _, rifle := range []string{"Gun_BoltActionRifle", "Gun_SniperRifle"} {
		t.Run(rifle, func(t *testing.T) {
			pawns := []EquipCandidatePawn{weaponPawn("novice", 2), weaponPawn("expert", 18)}
			weapons := []EquipCandidateWeapon{{Thing: "rifle", Definition: rifle, Class: WeaponRanged}, {Thing: "shotgun", Definition: "Gun_PumpShotgun", Class: WeaponRanged}}
			pairs := AssignEquip(pawns, weapons)
			if len(pairs) != 2 || pairs[0].Pawn != "expert" || pairs[0].Weapon.Thing != "rifle" || pairs[1].Pawn != "novice" || pairs[1].Weapon.Thing != "shotgun" {
				t.Fatal(pairs)
			}
			pawns[0], pawns[1] = pawns[1], pawns[0]
			weapons[0], weapons[1] = weapons[1], weapons[0]
			if got := AssignEquip(pawns, weapons); !reflect.DeepEqual(got, pairs) {
				t.Fatal("unstable", got, pairs)
			}
		})
	}
}

func TestWeaponAssignmentRestrictions(t *testing.T) {
	for _, tc := range []struct {
		name   string
		change func(*EquipCandidatePawn)
		want   string
	}{
		{"brawler", func(p *EquipCandidatePawn) { p.Profile.Effects.MeleeOnly = true }, "club"},
		{"shooting tag", func(p *EquipCandidatePawn) { p.ShootingDisabled = true }, "club"},
		{"disabled skill", func(p *EquipCandidatePawn) { p.Profile.Skills["Shooting"] = ProfileSkill{Disabled: true} }, "club"},
		{"violent", func(p *EquipCandidatePawn) { p.IncapableOfViolence = domain.Known(true) }, ""},
		{"unknown violence", func(p *EquipCandidatePawn) { p.IncapableOfViolence = domain.Unknown[bool]() }, ""},
		{"player assignment", func(p *EquipCandidatePawn) { p.Armed = domain.Known(true) }, ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			p := weaponPawn("pawn", 20)
			tc.change(&p)
			pairs := AssignEquip([]EquipCandidatePawn{p}, []EquipCandidateWeapon{{Thing: "gun", Definition: "Gun_SniperRifle", Class: WeaponRanged}, {Thing: "club", Class: WeaponMelee}})
			if tc.want == "" {
				if len(pairs) != 0 {
					t.Fatal(pairs)
				}
			} else if len(pairs) != 1 || pairs[0].Weapon.Thing != tc.want {
				t.Fatal(pairs)
			}
		})
	}
}

func TestWeaponBiocodeAndAreaFire(t *testing.T) {
	a, b := weaponPawn("a", 20), weaponPawn("b", 5)
	w := EquipCandidateWeapon{Thing: "coded", Definition: "Gun_BoltActionRifle", Class: WeaponRanged, BiocodedTo: "b"}
	pairs := AssignEquip([]EquipCandidatePawn{a, b}, []EquipCandidateWeapon{w})
	if len(pairs) != 1 || pairs[0].Pawn != "b" {
		t.Fatal(pairs)
	}
	b.Current = &w
	b.Armed = domain.Known(true)
	b.AutomationOwned = true
	if pairs = AssignEquip([]EquipCandidatePawn{b}, []EquipCandidateWeapon{{Thing: "other", Definition: "Gun_SniperRifle", Class: WeaponRanged}}); len(pairs) != 0 {
		t.Fatal("displaced biocode", pairs)
	}
	for def, profile := range weaponProfiles {
		if !profile.ForcedMiss {
			continue
		}
		w = EquipCandidateWeapon{Thing: "area", Definition: def, Class: WeaponRanged}
		if ScoreWeapon(a, w) != 0 {
			t.Fatal("unsafe area fire", def)
		}
	}
	a.LoneFighter = true
	if ScoreWeapon(a, EquipCandidateWeapon{Definition: "Gun_Minigun", Class: WeaponRanged}) <= 0 {
		t.Fatal("explicit lone fighter rejected")
	}
}

func TestWeaponUpgradeThresholdAndArmor(t *testing.T) {
	p := weaponPawn("pawn", 18)
	old := EquipCandidateWeapon{Thing: "old", Definition: "Bow_Short", Class: WeaponRanged}
	p.Current = &old
	p.Armed = domain.Known(true)
	p.AutomationOwned = true
	weapons := []EquipCandidateWeapon{{Thing: "same", Definition: "Bow_Short", Class: WeaponRanged}, {Thing: "upgrade", Definition: "Gun_BoltActionRifle", Class: WeaponRanged}}
	if got := AssignEquip([]EquipCandidatePawn{p}, weapons); len(got) != 1 || got[0].Weapon.Thing != "upgrade" {
		t.Fatal(got)
	}
	if got := AssignEquip([]EquipCandidatePawn{p}, weapons[:1]); len(got) != 0 {
		t.Fatal("churn", got)
	}
	p.AutomationOwned = false
	if got := AssignEquip([]EquipCandidatePawn{p}, weapons); len(got) != 0 {
		t.Fatal("player weapon", got)
	}
	w := weapons[1]
	base := ScoreWeapon(p, w)
	p.RaidArmor = domain.Known(.8)
	if got := ScoreWeapon(p, w); got >= base || got <= 0 {
		t.Fatal(got, base)
	}
	p.RaidArmor = domain.Unknown[float64]()
	p.Role = WeaponRoleSniper
	if ScoreWeapon(p, w) <= base {
		t.Fatal("role ignored")
	}
}

func TestWeaponProductionDemandNetsAssignmentsAndResearch(t *testing.T) {
	pawns := []EquipCandidatePawn{weaponPawn("a", 2), weaponPawn("b", 2), weaponPawn("c", 2)}
	pawns[0].Profile.Skills["Melee"] = ProfileSkill{}
	pawns[1].Profile.Skills["Melee"] = ProfileSkill{}
	pawns[2].Profile.Effects.MeleeOnly = true
	recipes := []GearRecipe{
		{Products: []Resource{"Bow_Short"}, Available: domain.Known(true), AvailableOn: domain.Known(true)},
		{Products: []Resource{"MeleeWeapon_Club"}, Available: domain.Known(true), AvailableOn: domain.Known(true)},
		{Products: []Resource{"Gun_AssaultRifle"}, Available: domain.Known(false), AvailableOn: domain.Known(true)},
	}
	weapons := []EquipCandidateWeapon{{Thing: "bow", Definition: "Bow_Short", Class: WeaponRanged}}
	want := []Amount{{Resource: "Bow_Short", Count: 1}, {Resource: "MeleeWeapon_Club", Count: 1}}
	if got := WeaponProductionDemand(pawns, weapons, recipes); !reflect.DeepEqual(got, want) {
		t.Fatal(got, want)
	}
	if got := WeaponProductionDemand(pawns, nil, nil); len(got) != 0 {
		t.Fatal("invented recipe", got)
	}
}
