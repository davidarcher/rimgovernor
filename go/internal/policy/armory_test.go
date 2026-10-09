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

// testArmoryRungs are the rungs the bridge gives these recipes from their
// research and tables (bridge.TestArmoryWeaponTierMatchesRetiredTable).
var testArmoryRungs = map[Resource]ArmoryTier{
	"Bow_Short": ArmoryTierNeolithic, "MeleeWeapon_Club": ArmoryTierNeolithic,
	"Bow_Great": ArmoryTierSmithing, "Gun_Revolver": ArmoryTierMachining,
	"Gun_AssaultRifle": ArmoryTierMachining, "Gun_ChargeRifle": ArmoryTierFabrication,
}

func armoryRecipe(def Resource, available bool) GearRecipe {
	return GearRecipe{Definition: "Make_" + string(def), Products: []Resource{def}, Available: domain.Known(available), AvailableOn: domain.Known(true), Armory: testArmoryRungs[def]}
}

// armoryProduct is the facts of a recipe's weapon as the bridge reads them: a
// weapon by trade.
func armoryProduct(def Resource) WeaponDef {
	facts := coreFacts(string(def))
	facts.ByTrade = true
	return facts
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
	revolver := map[domain.PawnID]ArmoryPrimary{"a": {Definition: "Gun_Revolver", Ranged: true, Quality: 2, Facts: coreFacts("Gun_Revolver")}}
	products := map[Resource]WeaponDef{}
	for _, r := range recipes {
		products[r.Products[0]] = armoryProduct(r.Products[0])
	}
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
		{"revolver upgraded to the best smithing weapon that scores higher", ArmoryTierSmithing, recipes, revolver, armed, "Bow_Great"},
		{"armed at unknown tier", ArmoryTierUnknown, recipes, revolver, armed, ""},
		{"rifle unresearched", ArmoryTierMachining, []GearRecipe{armoryRecipe("Gun_Revolver", true), armoryRecipe("Gun_AssaultRifle", false)}, revolver, armed, ""},
		{"legendary revolver still loses to a strictly higher score", ArmoryTierMachining, recipes, map[domain.PawnID]ArmoryPrimary{"a": {Definition: "Gun_Revolver", Ranged: true, Quality: 6, Facts: coreFacts("Gun_Revolver")}}, armed, "Gun_AssaultRifle"},
		{"primary unobserved", ArmoryTierMachining, recipes, nil, armed, ""},
	} {
		var want []Amount
		if tc.want != "" {
			want = []Amount{{Resource: tc.want, Count: 1}}
		}
		got, _ := ArmoryWeaponDemand(tc.tier, []EquipCandidatePawn{tc.pawn}, tc.primaries, nil, tc.recipes, products)
		if len(got) != len(want) || len(got) == 1 && got[0] != want[0] {
			t.Errorf("%s: got %v want %v", tc.name, got, want)
		}
	}
	// A loose rifle is gear's to wear; the armory bills none.
	loose := []EquipCandidateWeapon{{Thing: "r", Definition: "Gun_AssaultRifle", Class: WeaponRanged, Facts: coreFacts("Gun_AssaultRifle")}}
	if got, _ := ArmoryWeaponDemand(ArmoryTierMachining, []EquipCandidatePawn{armed}, revolver, loose, recipes, products); len(got) != 0 {
		t.Error("billed over a loose upgrade", got)
	}
	// Loose weapons arm the unarmed first.
	if got, _ := ArmoryWeaponDemand(ArmoryTierMachining, []EquipCandidatePawn{unarmed}, nil, loose, recipes, products); len(got) != 0 {
		t.Error("billed for an armable colonist", got)
	}
}

// A hunter without a hunting-reach ranged weapon (unarmed, melee or a short
// gun) is hunter demand, owned by food; a hunter's upgrade of an adequate bow
// and a plain fighter's demand stay fighter demand.
func TestArmoryWeaponDemandSplitsHunters(t *testing.T) {
	recipes := []GearRecipe{armoryRecipe("Bow_Short", true), armoryRecipe("MeleeWeapon_Club", true), armoryRecipe("Gun_AssaultRifle", true)}
	products := map[Resource]WeaponDef{}
	for _, r := range recipes {
		products[r.Products[0]] = armoryProduct(r.Products[0])
	}
	hunter := weaponPawn("h", 10)
	hunter.Role = WeaponRoleHunter
	fighter := weaponPawn("f", 10)
	fighter.Profile.Skills["Melee"] = ProfileSkill{}
	fighters, hunters := ArmoryWeaponDemand(ArmoryTierNeolithic, []EquipCandidatePawn{hunter, fighter}, nil, nil, recipes, products)
	if len(hunters) != 1 || hunters[0].Resource != "Bow_Short" || hunters[0].Count != 1 || len(fighters) != 1 || fighters[0].Count != 1 {
		t.Fatalf("unarmed hunter and fighter: fighters %v hunters %v", fighters, hunters)
	}
	melee := hunter
	melee.Armed = domain.Known(true)
	club := map[domain.PawnID]ArmoryPrimary{"h": {Definition: "MeleeWeapon_Club", Quality: 2, Facts: coreFacts("MeleeWeapon_Club")}}
	if _, hunters = ArmoryWeaponDemand(ArmoryTierNeolithic, []EquipCandidatePawn{melee}, club, nil, recipes, products); len(hunters) != 1 {
		t.Fatalf("a hunter with a club wants a bow: %v", hunters)
	}
	bowed := map[domain.PawnID]ArmoryPrimary{"h": {Definition: "Bow_Short", Ranged: true, Quality: 2, Facts: coreFacts("Bow_Short")}}
	if fighters, hunters = ArmoryWeaponDemand(ArmoryTierMachining, []EquipCandidatePawn{melee}, bowed, nil, recipes, products); len(hunters) != 0 {
		t.Fatalf("a bow hunter's upgrade is not hunter demand: %v %v", fighters, hunters)
	}
}
