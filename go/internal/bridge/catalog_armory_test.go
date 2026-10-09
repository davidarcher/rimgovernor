package bridge

import (
	"slices"
	"sort"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// retiredArmoryWeaponTiers is policy's armoryWeaponTiers as it stood before
// the recipes' research and tables replaced it, typed by hand.
var retiredArmoryWeaponTiers = map[string]policy.ArmoryTier{
	"MeleeWeapon_Club": policy.ArmoryTierNeolithic, "MeleeWeapon_Knife": policy.ArmoryTierNeolithic,
	"Bow_Short": policy.ArmoryTierNeolithic, "Bow_Recurve": policy.ArmoryTierNeolithic,
	"MeleeWeapon_Gladius": policy.ArmoryTierSmithing, "MeleeWeapon_Longsword": policy.ArmoryTierSmithing,
	"MeleeWeapon_LongSword": policy.ArmoryTierSmithing, "MeleeWeapon_Mace": policy.ArmoryTierSmithing,
	"MeleeWeapon_Spear": policy.ArmoryTierSmithing, "Bow_Great": policy.ArmoryTierSmithing,
	"Gun_Revolver": policy.ArmoryTierMachining, "Gun_Autopistol": policy.ArmoryTierMachining,
	"Gun_PumpShotgun": policy.ArmoryTierMachining, "Gun_BoltActionRifle": policy.ArmoryTierMachining,
	"Gun_MachinePistol": policy.ArmoryTierMachining, "Gun_HeavySMG": policy.ArmoryTierMachining,
	"Gun_AssaultRifle": policy.ArmoryTierMachining, "Gun_SniperRifle": policy.ArmoryTierMachining,
	"Gun_ChainShotgun": policy.ArmoryTierMachining,
	"Gun_ChargeRifle":  policy.ArmoryTierFabrication, "Gun_ChargeLance": policy.ArmoryTierFabrication,
}

// armoryTierChanges explains every weapon whose rung differs between the
// retired table and the rule over the recipe rows. An entry is a weapon the
// table never modelled (old Unknown) that the rule now models, or a table
// entry no recipe backs.
var armoryTierChanges = map[string]struct {
	Old, New policy.ArmoryTier
	Why      string
}{
	"MeleeWeapon_Longsword": {policy.ArmoryTierSmithing, policy.ArmoryTierUnknown, "a misspelling of MeleeWeapon_LongSword: no such def, no recipe"},
	"MeleeWeapon_Axe":       {policy.ArmoryTierUnknown, policy.ArmoryTierSmithing, "forged melee weapon (Smithing, smithy) the table omitted"},
	"MeleeWeapon_BreachAxe": {policy.ArmoryTierUnknown, policy.ArmoryTierSmithing, "forged melee weapon (Smithing, smithy) the table omitted"},
	"MeleeWeapon_Ikwa":      {policy.ArmoryTierUnknown, policy.ArmoryTierSmithing, "forged melee weapon (Smithing, smithy) the table omitted"},
	"MeleeWeapon_Warhammer": {policy.ArmoryTierUnknown, policy.ArmoryTierSmithing, "forged melee weapon (LongBlades, smithy) the table omitted"},
	"Pila":                  {policy.ArmoryTierUnknown, policy.ArmoryTierSmithing, "thrown spear with no research of its own, made at the smithy (Smithing): a plain ranged weapon the table omitted"},
	"Gun_LMG":               {policy.ArmoryTierUnknown, policy.ArmoryTierMachining, "machined gun (GasOperation) the table omitted"},
	"Gun_Minigun":           {policy.ArmoryTierUnknown, policy.ArmoryTierMachining, "machined gun (MultibarrelWeapons) the table omitted"},
	"Gun_BeamGraser":        {policy.ArmoryTierUnknown, policy.ArmoryTierFabrication, "fabricated beam weapon (BeamWeapons, spacer) the table omitted"},
	"Gun_BeamRepeater":      {policy.ArmoryTierUnknown, policy.ArmoryTierFabrication, "fabricated beam weapon (BeamWeapons, spacer) the table omitted"},
}

// Every weapon a recipe in the recorded catalog makes has the rung the
// recipe's research and work table give it, and every difference from the
// retired table is accounted for. Explosives, incendiaries and the Anomaly
// bioferrite weapons (research off the tech ladder) are not modelled.
func TestArmoryWeaponTierMatchesRetiredTable(t *testing.T) {
	catalog := fullCatalog(t)
	products := map[string]bool{}
	for _, raw := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		defs, err := recipeProductDefs(raw.(*d.RecipeDef))
		if err != nil {
			t.Fatal(err)
		}
		for _, def := range defs {
			products[string(def)] = true
		}
	}
	for def := range retiredArmoryWeaponTiers {
		products[def] = true
	}
	names := make([]string, 0, len(products))
	for def := range products {
		names = append(names, def)
	}
	sort.Strings(names)
	var changed []string
	for _, def := range names {
		old := retiredArmoryWeaponTiers[def]
		got := policy.ArmoryTierUnknown
		if catalog.ThingDef(def) != nil {
			tier, _, err := catalog.ArmoryWeaponTier(def)
			if err != nil {
				t.Fatalf("%s: %v", def, err)
			}
			got = tier
		}
		if got == old {
			continue
		}
		changed = append(changed, def)
		want, explained := armoryTierChanges[def]
		if !explained || want.Old != old || want.New != got {
			t.Errorf("%s: retired %v, rule %v, unexplained or mis-explained (%+v)", def, old, got, want)
		}
	}
	for def := range armoryTierChanges {
		if !slices.Contains(changed, def) {
			t.Errorf("%s is explained but did not change", def)
		}
	}
}
