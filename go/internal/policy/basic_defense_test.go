package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Two armed colonists met the Foothold gate and recovered EnsureBasicDefense
// while a starting club lay on the ground beside an unarmed third: an
// unarmed fighter keeps the goal owed, the gate itself stays at two.
func TestBasicDefenseOwedWhileAFighterIsUnarmed(t *testing.T) {
	if v, k := basicDefenseRecovered(domain.Known(true), domain.Known(int64(1))).Value(); !k || v {
		t.Fatal("unarmed fighter recovered defense", v, k)
	}
	if v, k := basicDefenseRecovered(domain.Known(true), domain.Known(int64(0))).Value(); !k || !v {
		t.Fatal(v, k)
	}
	if v, k := basicDefenseRecovered(domain.Known(true), domain.Unknown[int64]()).Value(); !k || !v {
		t.Fatal("unknown unarmed count overrode the gate", v, k)
	}
	if _, k := basicDefenseRecovered(domain.Unknown[bool](), domain.Known(int64(2))).Value(); k {
		t.Fatal("unknown gate became known")
	}
	p := DefaultRoutinePolicy()
	f := RoutineFacts{Colonists: domain.Known(int64(3)), Armed: domain.Known(int64(2)), Unarmed: domain.Known(int64(1))}
	if v, k := RoutineDevelopmentDeficit(EnsureBasicDefense, f, p).Value(); !k || v <= 0 {
		t.Fatal("unarmed fighter left no development deficit", v, k)
	}
}

func TestUnarmedFightersAndWeaponRecipes(t *testing.T) {
	pawns := []EquipCandidatePawn{weaponPawn("a", 5), weaponPawn("b", 5)}
	pawns[1].IncapableOfViolence = domain.Known(true)
	if n := UnarmedFighters(pawns, nil); n != 1 {
		t.Fatal(n)
	}
	club := []EquipCandidateWeapon{{Thing: "club", Definition: "MeleeWeapon_Club", Class: WeaponMelee}}
	if n := UnarmedFighters(pawns, club); n != 0 {
		t.Fatal("a loose club still left a fighter to craft for", n)
	}
	if !WeaponRecipe(GearRecipe{Products: []Resource{"MeleeWeapon_Club"}}) || WeaponRecipe(GearRecipe{Products: []Resource{"Apparel_TribalA"}}) {
		t.Fatal("weapon recipe misclassified")
	}
}
