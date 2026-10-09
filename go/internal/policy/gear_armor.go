package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ArmorResearchRungs is the armor ladder a colony with a soldier walks
// right after Electricity: the simple helmet's smithy, the tailoring
// bench flak cloth needs, then flak armor itself. It stays a judgment table
// (DefTables): the recipes' prerequisites give Smithing and FlakArmor, but
// when a colony wants them is a choice and ComplexClothing is no
// prerequisite of any armor recipe. It once also named "Shields", which is
// no project (the belt's is ShieldBelt), so the rung never matched.
var ArmorResearchRungs = []string{"Smithing", "ComplexClothing", "FlakArmor"}

// ArmorResearchLadder is ladder with ArmorResearchRungs spliced in directly
// after Electricity (at the front when the ladder has no Electricity rung)
// while a soldier role exists; rungs the ladder already names move up
// rather than repeat. Without a soldier the ladder is returned as is.
func ArmorResearchLadder(ladder []string, soldier bool) []string {
	if !soldier {
		return ladder
	}
	armor := map[string]bool{}
	for _, rung := range ArmorResearchRungs {
		armor[rung] = true
	}
	out := []string{}
	inserted := false
	insert := func() {
		if !inserted {
			out = append(out, ArmorResearchRungs...)
			inserted = true
		}
	}
	for _, rung := range ladder {
		if armor[rung] {
			continue
		}
		out = append(out, rung)
		if rung == "Electricity" {
			insert()
		}
	}
	if !inserted {
		out = append(append([]string{}, ArmorResearchRungs...), out...)
	}
	return out
}

// ArmorResearchPolicy is p with its ResearchLadder extended by
// ArmorResearchLadder; an empty ladder (roadmap disabled) stays empty.
func ArmorResearchPolicy(p RoundsPolicy, soldier bool) RoundsPolicy {
	if len(p.ResearchLadder) == 0 {
		return p
	}
	p.ResearchLadder = ArmorResearchLadder(p.ResearchLadder, soldier)
	return p
}

// GearSoldierPresent reports whether the gear census derives a soldier role
// for any pawn (its apparel-policy role, or its loadout model's when one is
// supplied). Unknown census: no soldier.
func GearSoldierPresent(gear domain.Fact[GearObservation]) bool {
	v, known := gear.Value()
	if !known {
		return false
	}
	for _, p := range v.Pawns {
		if model, ok := p.LoadoutModel.Value(); ok && DeriveGearRole(model.Role) == GearSoldier {
			return true
		}
		if state, ok := p.Policy.Value(); ok && DeriveGearRole(state.Role) == GearSoldier {
			return true
		}
	}
	return false
}

// armoryArmorRung is one step of an armor family's ladder: the
// definition and the lowest armory tier that may craft it. Rungs run from
// the cheapest to the best; a family's need falls back down its rungs when
// the tier cannot reach the higher one.
type armoryArmorRung struct {
	Definition Resource
	Tier       ArmoryTier
}

// armoryArmorFamilies is the body-armor and helmet ladder the armory owns:
// flak -> recon -> marine, with the simple helmet at smithing.
var armoryArmorFamilies = [][]armoryArmorRung{
	{{"Apparel_FlakVest", ArmoryTierMachining}, {"Apparel_FlakJacket", ArmoryTierMachining}, {"Apparel_ArmorRecon", ArmoryTierFabrication}, {"Apparel_PowerArmor", ArmoryTierFabrication}},
	{{"Apparel_FlakPants", ArmoryTierMachining}},
	{{"Apparel_SimpleHelmet", ArmoryTierSmithing}, {"Apparel_AdvancedHelmet", ArmoryTierMachining}, {"Apparel_ArmorHelmetRecon", ArmoryTierFabrication}, {"Apparel_PowerArmorHelmet", ArmoryTierFabrication}},
}

// armoryArmorRungs is the rungs a need for definition may be crafted as
// under tier, best first: the need's own rung and every lower one the tier
// allows. ok is false for a definition outside the armory's ladder.
func armoryArmorRungs(definition Resource, tier ArmoryTier) (rungs []Resource, ok bool) {
	for _, family := range armoryArmorFamilies {
		for i, rung := range family {
			if rung.Definition != definition {
				continue
			}
			for j := i; j >= 0; j-- {
				if family[j].Tier <= tier {
					rungs = append(rungs, family[j].Definition)
				}
			}
			return rungs, true
		}
	}
	return nil, false
}

// ArmoryArmor reports whether definition is body armor or a helmet the
// armory crafts; the gear planner leaves its bills to the armory.
func ArmoryArmor(definition Resource) bool {
	_, ok := armoryArmorRungs(definition, ArmoryTierFabrication)
	return ok
}
