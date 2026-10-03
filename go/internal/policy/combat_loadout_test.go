package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func loadoutDefender(id domain.PawnID, shooting int, primary string) LoadoutDefender {
	return LoadoutDefender{EquipCandidatePawn: weaponPawn(id, shooting), Primary: primary, PrimaryFacts: coreWeapons[primary]}
}

func loose(thing, def string, class WeaponClass) EquipCandidateWeapon {
	return EquipCandidateWeapon{Thing: thing, Definition: def, Class: class, Cell: domain.Cell{X: 5, Z: 5}, Facts: coreWeapons[def]}
}

func orderPairs(orders []LoadoutOrder) [][3]string {
	var out [][3]string
	for _, o := range orders {
		kind := "equip"
		if o.Wear {
			kind = "wear"
		}
		out = append(out, [3]string{string(o.Pawn), kind, o.Thing})
	}
	return out
}

func TestLoadoutPods(t *testing.T) {
	view := CombatView{Pods: domain.Known(PodArrival{})}
	if got := ClassifyLoadoutThreat(view, false); got != LoadoutPods {
		t.Fatalf("threat = %q", got)
	}
	defenders := []LoadoutDefender{
		loadoutDefender("a", 8, "Gun_BoltActionRifle"),
		loadoutDefender("b", 8, "Gun_ChainShotgun"), // already close/high DPS
		loadoutDefender("c", 8, ""),
	}
	weapons := []EquipCandidateWeapon{
		loose("sniper", "Gun_SniperRifle", WeaponRanged),
		loose("chain", "Gun_ChainShotgun", WeaponRanged),
		loose("smg", "Gun_HeavySMG", WeaponRanged),
	}
	got := orderPairs(ThreatLoadout(LoadoutPods, defenders, weapons, nil))
	want := [][3]string{{"a", "equip", "chain"}, {"c", "equip", "smg"}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("orders = %v, want %v", got, want)
	}
}

func TestLoadoutTribals(t *testing.T) {
	view := CombatView{Pawns: []CombatPawnState{{ID: "h1", Kind: "Tribal_Archer", Weapon: "Bow_Great"}}, Threats: []SquadThreatFacts{{ID: "h1"}}}
	if got := ClassifyLoadoutThreat(view, false); got != LoadoutTribal {
		t.Fatalf("threat = %q", got)
	}
	defenders := []LoadoutDefender{
		loadoutDefender("a", 10, "Gun_PumpShotgun"),
		loadoutDefender("b", 10, "Gun_SniperRifle"), // already out-ranges
		loadoutDefender("c", 10, "Bow_Recurve"),
	}
	weapons := []EquipCandidateWeapon{
		loose("bolt", "Gun_BoltActionRifle", WeaponRanged),
		loose("ar", "Gun_AssaultRifle", WeaponRanged), // 31 tiles: too short
		loose("sniper", "Gun_SniperRifle", WeaponRanged),
	}
	got := orderPairs(ThreatLoadout(LoadoutTribal, defenders, weapons, nil))
	want := [][3]string{{"a", "equip", "bolt"}, {"c", "equip", "sniper"}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("orders = %v, want %v", got, want)
	}
}

func TestLoadoutEMPCarrier(t *testing.T) {
	for name, view := range map[string]CombatView{
		"mechs":          {Pawns: []CombatPawnState{{ID: "m", Kind: "Mech_Scyther"}}, Threats: []SquadThreatFacts{{ID: "m"}}},
		"shielded melee": {Pawns: []CombatPawnState{{ID: "p", Kind: "Pirate", Weapon: "MeleeWeapon_Longsword", Shield: domain.Known(1.0)}}, Threats: []SquadThreatFacts{{ID: "p"}}},
	} {
		if got := ClassifyLoadoutThreat(view, false); got != LoadoutEMP {
			t.Fatalf("%s: threat = %q", name, got)
		}
	}
	brawler := loadoutDefender("a", 15, "MeleeWeapon_Longsword")
	brawler.Role = WeaponRoleMelee
	defenders := []LoadoutDefender{
		brawler, // best shooter but a brawler: never the carrier
		loadoutDefender("b", 6, "Gun_AssaultRifle"),
		loadoutDefender("c", 12, "Gun_AssaultRifle"),
		{EquipCandidatePawn: weaponPawn("d", 12), Primary: "Gun_AssaultRifle", Squad: "west"},
		{EquipCandidatePawn: weaponPawn("e", 3), Primary: "Weapon_GrenadeEMP", PrimaryFacts: coreWeapons["Weapon_GrenadeEMP"], Squad: "east"}, // east already carries
		{EquipCandidatePawn: weaponPawn("f", 9), Primary: "Gun_AssaultRifle", Squad: "east"},
	}
	weapons := []EquipCandidateWeapon{
		loose("launcher", "Gun_EmpLauncher", WeaponRanged),
		loose("emp1", "Weapon_GrenadeEMP", WeaponRanged),
		loose("emp2", "Weapon_GrenadeEMP", WeaponRanged),
	}
	got := orderPairs(ThreatLoadout(LoadoutEMP, defenders, weapons, nil))
	want := [][3]string{{"c", "equip", "emp1"}, {"d", "equip", "emp2"}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("orders = %v, want %v", got, want)
	}
}

func TestLoadoutShieldBeltMelee(t *testing.T) {
	brawler := loadoutDefender("a", 5, "MeleeWeapon_Mace")
	belted := loadoutDefender("b", 5, "MeleeWeapon_Longsword")
	belted.ShieldBelt = true
	warden := loadoutDefender("w", 5, "Gun_Revolver")
	warden.Warden = true
	defenders := []LoadoutDefender{brawler, belted, loadoutDefender("c", 10, "Gun_AssaultRifle"), warden}
	belts := []LoadoutApparel{{Thing: "belt1", Definition: "Apparel_ShieldBelt"}, {Thing: "belt2", Definition: "Apparel_ShieldBelt"}}
	weapons := []EquipCandidateWeapon{loose("club", "MeleeWeapon_Club", WeaponMelee), loose("knife", "MeleeWeapon_Knife", WeaponMelee)}
	// A prison break: the warden takes the club (blunt, not the knife),
	// then the melee brawler without a belt wears one.
	got := orderPairs(ThreatLoadout(ClassifyLoadoutThreat(CombatView{}, true), defenders, weapons, belts))
	want := [][3]string{{"w", "equip", "club"}, {"a", "wear", "belt1"}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("orders = %v, want %v", got, want)
	}
	if got := orderPairs(ThreatLoadout(LoadoutNone, defenders, nil, nil)); got != nil {
		t.Fatalf("no belts stocked: orders = %v", got)
	}
}
