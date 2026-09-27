package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSelectEquipPairsNearestWeaponToLowestPawnID(t *testing.T) {
	pawns := []EquipCandidatePawn{
		{Pawn: "armed", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(true), Position: domain.Cell{X: 0, Z: 0}},
		{Pawn: "b-unarmed", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false), Position: domain.Cell{X: 0, Z: 0}},
		{Pawn: "a-unarmed", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false), Position: domain.Cell{X: 5, Z: 5}},
	}
	weapons := []EquipCandidateWeapon{
		{Thing: "far", Definition: "Gun_Revolver", Cell: domain.Cell{X: 50, Z: 50}, Class: WeaponRanged},
		{Thing: "near", Definition: "Gun_Revolver", Cell: domain.Cell{X: 6, Z: 5}, Class: WeaponRanged},
	}
	pawn, weapon, ok := SelectEquip(pawns, weapons)
	if !ok || pawn != "a-unarmed" || weapon.Thing != "near" {
		t.Fatal(pawn, weapon, ok)
	}
}

func TestSelectEquipRequiresEligiblePawnAndWeapon(t *testing.T) {
	pawns := []EquipCandidatePawn{
		{Pawn: "armed", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(true)},
	}
	weapons := []EquipCandidateWeapon{{Thing: "gun", Definition: "Gun_Revolver"}}
	if _, _, ok := SelectEquip(pawns, weapons); ok {
		t.Fatal("expected no eligible pawn")
	}
	if _, _, ok := SelectEquip(nil, weapons); ok {
		t.Fatal("expected no pawns")
	}
	unarmed := []EquipCandidatePawn{{Pawn: "u", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false)}}
	if _, _, ok := SelectEquip(unarmed, nil); ok {
		t.Fatal("expected no weapons")
	}
}

// A wood log at the pawn's feet is equippable but not a weapon: the bows a
// few cells away come first (colony-6 armed a colonist with the log).
func TestSelectEquipPrefersRealWeaponsOverMakeshift(t *testing.T) {
	pawns := []EquipCandidatePawn{{Pawn: "a", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), IncapableOfViolence: domain.Known(false), Armed: domain.Known(false), Position: domain.Cell{X: 5, Z: 5}}}
	weapons := []EquipCandidateWeapon{
		{Thing: "log", Definition: "WoodLog", Cell: domain.Cell{X: 5, Z: 6}},
		{Thing: "club", Definition: "MeleeWeapon_Club", Cell: domain.Cell{X: 6, Z: 6}, Class: WeaponMelee},
		{Thing: "bow-far", Definition: "Modded_Sling", Cell: domain.Cell{X: 20, Z: 5}, Class: WeaponRanged},
		{Thing: "bow-near", Definition: "Modded_Sling", Cell: domain.Cell{X: 9, Z: 5}, Class: WeaponRanged},
	}
	_, weapon, ok := SelectEquip(pawns, weapons)
	if !ok || weapon.Thing != "bow-near" {
		t.Fatal(weapon, ok)
	}
	_, weapon, _ = SelectEquip(pawns, weapons[:2])
	if weapon.Thing != "club" {
		t.Fatal(weapon)
	}
	if ClassifyWeapon(true, true, false) != WeaponRanged || ClassifyWeapon(false, true, false) != WeaponRanged || ClassifyWeapon(true, false, true) != WeaponMelee || ClassifyWeapon(false, false, true) != WeaponMakeshift || ClassifyWeapon(false, false, false) != WeaponMakeshift {
		t.Fatal("classification")
	}
}
