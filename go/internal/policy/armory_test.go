package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestAssessArmoryTiers(t *testing.T) {
	all := domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Smithing", "Machining", "Fabrication"}})
	for _, tc := range []struct {
		name     string
		points   domain.Fact[float64]
		research domain.Fact[ResearchFacts]
		want     ArmoryAssessment
	}{
		{"unknown points", domain.Unknown[float64](), all, ArmoryAssessment{ArmoryTierUnknown, ArmoryTierFabrication, ArmoryTierUnknown}},
		{"NaN points", domain.Known(math.NaN()), all, ArmoryAssessment{ArmoryTierUnknown, ArmoryTierFabrication, ArmoryTierUnknown}},
		{"low", domain.Known(35.0), all, ArmoryAssessment{ArmoryTierNeolithic, ArmoryTierFabrication, ArmoryTierNeolithic}},
		{"smithing threshold", domain.Known(300.0), all, ArmoryAssessment{ArmoryTierSmithing, ArmoryTierFabrication, ArmoryTierSmithing}},
		{"machining threshold", domain.Known(800.0), all, ArmoryAssessment{ArmoryTierMachining, ArmoryTierFabrication, ArmoryTierMachining}},
		{"just below fabrication", domain.Known(2499.0), all, ArmoryAssessment{ArmoryTierMachining, ArmoryTierFabrication, ArmoryTierMachining}},
		{"fabrication", domain.Known(2500.0), all, ArmoryAssessment{ArmoryTierFabrication, ArmoryTierFabrication, ArmoryTierFabrication}},
		{"research caps", domain.Known(5000.0), domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Smithing"}}), ArmoryAssessment{ArmoryTierFabrication, ArmoryTierSmithing, ArmoryTierSmithing}},
		{"gap stops ladder", domain.Known(5000.0), domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Machining", "Fabrication"}}), ArmoryAssessment{ArmoryTierFabrication, ArmoryTierNeolithic, ArmoryTierNeolithic}},
		{"unknown research", domain.Known(5000.0), domain.Unknown[ResearchFacts](), ArmoryAssessment{ArmoryTierFabrication, ArmoryTierNeolithic, ArmoryTierNeolithic}},
	} {
		if got := AssessArmory(tc.points, tc.research); got != tc.want {
			t.Errorf("%s: got %+v want %+v", tc.name, got, tc.want)
		}
	}
}

func armoryRecipe(def Resource, available bool) GearRecipe {
	return GearRecipe{Definition: "Make_" + string(def), Products: []Resource{def}, Available: domain.Known(available), AvailableOn: domain.Known(true)}
}

// The ladder per tier and research: the unarmed get the best recipe at or
// under the tier (an unknown tier arms at neolithic), the armed upgrade only
// at a known tier and past the quality-scaled swap gain.
func TestArmoryWeaponDemandLadder(t *testing.T) {
	recipes := []GearRecipe{armoryRecipe("Bow_Short", true), armoryRecipe("MeleeWeapon_Club", true), armoryRecipe("Bow_Great", true), armoryRecipe("Gun_Revolver", true), armoryRecipe("Gun_AssaultRifle", true), armoryRecipe("Gun_ChargeRifle", true)}
	unarmed := weaponPawn("u", 10)
	unarmed.Profile.Skills["Melee"] = ProfileSkill{}
	armed := weaponPawn("a", 10)
	armed.Profile.Skills["Melee"] = ProfileSkill{}
	armed.Armed = domain.Known(true)
	revolver := map[domain.PawnID]ArmoryPrimary{"a": {Definition: "Gun_Revolver", Ranged: true, Quality: 2}}
	for _, tc := range []struct {
		name      string
		tier      ArmoryTier
		recipes   []GearRecipe
		primaries map[domain.PawnID]ArmoryPrimary
		pawn      EquipCandidatePawn
		want      Resource
	}{
		{"unknown tier arms neolithic", ArmoryTierUnknown, recipes, nil, unarmed, "Bow_Short"},
		{"neolithic", ArmoryTierNeolithic, recipes, nil, unarmed, "Bow_Short"},
		{"smithing", ArmoryTierSmithing, recipes, nil, unarmed, "Bow_Great"},
		{"machining", ArmoryTierMachining, recipes, nil, unarmed, "Gun_AssaultRifle"},
		{"fabrication", ArmoryTierFabrication, recipes, nil, unarmed, "Gun_ChargeRifle"},
		{"research missing", ArmoryTierMachining, []GearRecipe{armoryRecipe("Bow_Great", true), armoryRecipe("Gun_AssaultRifle", false)}, nil, unarmed, "Bow_Great"},
		{"revolver to rifle at machining", ArmoryTierMachining, recipes, revolver, armed, "Gun_AssaultRifle"},
		{"revolver kept below machining", ArmoryTierSmithing, recipes, revolver, armed, ""},
		{"armed at unknown tier", ArmoryTierUnknown, recipes, revolver, armed, ""},
		{"rifle unresearched", ArmoryTierMachining, []GearRecipe{armoryRecipe("Gun_Revolver", true), armoryRecipe("Gun_AssaultRifle", false)}, revolver, armed, ""},
		{"legendary revolver kept", ArmoryTierMachining, recipes, map[domain.PawnID]ArmoryPrimary{"a": {Definition: "Gun_Revolver", Ranged: true, Quality: 6}}, armed, ""},
		{"primary unobserved", ArmoryTierMachining, recipes, nil, armed, ""},
	} {
		var want []Amount
		if tc.want != "" {
			want = []Amount{{Resource: tc.want, Count: 1}}
		}
		got := ArmoryWeaponDemand(tc.tier, []EquipCandidatePawn{tc.pawn}, tc.primaries, nil, tc.recipes)
		if len(got) != len(want) || len(got) == 1 && got[0] != want[0] {
			t.Errorf("%s: got %v want %v", tc.name, got, want)
		}
	}
	// A loose rifle is gear's to wear; the armory bills none.
	loose := []EquipCandidateWeapon{{Thing: "r", Definition: "Gun_AssaultRifle", Class: WeaponRanged}}
	if got := ArmoryWeaponDemand(ArmoryTierMachining, []EquipCandidatePawn{armed}, revolver, loose, recipes); len(got) != 0 {
		t.Error("billed over a loose upgrade", got)
	}
	// Loose weapons arm the unarmed first.
	if got := ArmoryWeaponDemand(ArmoryTierMachining, []EquipCandidatePawn{unarmed}, nil, loose, recipes); len(got) != 0 {
		t.Error("billed for an armable colonist", got)
	}
}

func TestArmoryWeaponTiersModelled(t *testing.T) {
	for def := range armoryWeaponTiers {
		if _, ok := weaponProfiles[def]; !ok {
			t.Error("tiered weapon without a profile", def)
		}
	}
}
